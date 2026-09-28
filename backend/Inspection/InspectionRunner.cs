using MTCVision.Backend.Camera;
using MTCVision.Backend.Integration;
using MTCVision.Backend.Recipes;

namespace MTCVision.Backend.Inspection;

/// <summary>
/// The continuous inspection loop, owned by the server rather than the browser.
///
/// A line does not stop because someone closed a browser tab. Running here means the camera keeps
/// being inspected, images keep being archived and the counters keep adding up while nothing is
/// watching; a client that reconnects simply asks what the state is and picks the display back up.
/// </summary>
public sealed class InspectionRunner(
    ICameraService camera,
    LatestFrameStore frames,
    RecipeStore recipes,
    ImageHistoryStore images,
    InspectionEngine engine,
    SessionStore sessions,
    InspectionActivity activity) : IAsyncDisposable
{
    /// <summary>Breather between cycles so the loop cannot monopolise the camera or the CPU.</summary>
    private static readonly TimeSpan IdleBetweenCycles = TimeSpan.FromMilliseconds(200);

    private readonly SemaphoreSlim _stateLock = new(1, 1);

    /// <summary>Held for the length of one single inspection, so a second trigger cannot overlap it.</summary>
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private Recipe? _recipe;

    private InspectionResult? _lastResult;
    private string? _lastError;
    private RunStatistics _statistics = RunStatistics.Empty;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>A single inspection is in progress.</summary>
    public bool IsBusy => _cycleLock.CurrentCount == 0;

    /// <summary>There is a recipe single inspections would be judged against.</summary>
    public bool HasActiveRecipe => _recipe is not null;

    /// <summary>
    /// A trigger from outside would be accepted right now: there is a recipe to judge against, the
    /// camera is up, and nothing else is inspecting.
    /// </summary>
    public bool CanTrigger => !IsRunning && !IsBusy && _recipe is not null && camera.IsConnected;

    public RunState GetState() => new(
        IsRunning,
        _recipe?.Id,
        _recipe?.Name,
        _lastResult,
        _lastError,
        _statistics);

    public async Task<RunState> StartAsync(string recipeId)
    {
        await _stateLock.WaitAsync();
        try
        {
            await StopCoreAsync();

            _recipe = await recipes.GetAsync(recipeId)
                ?? throw new CameraConfigurationException($"'{recipeId}' reçetesi bulunamadı.");

            if (!camera.IsConnected)
            {
                throw new CameraConfigurationException("Denetim için kamera bağlı olmalı.");
            }

            _lastResult = null;
            _lastError = null;
            _statistics = RunStatistics.Empty;

            _cancellation = new CancellationTokenSource();
            _loop = RunLoopAsync(_recipe, _cancellation.Token);
            sessions.Update(state => state with { RunningRecipeId = _recipe.Id });
            return GetState();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<RunState> StopAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            await StopCoreAsync();

            // Stopping is deliberate, so a restart should not undo it.
            sessions.Update(state => state with { RunningRecipeId = null });
            return GetState();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task ResetStatisticsAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            _statistics = RunStatistics.Empty;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>Forgets the last verdict and the counters, as when the console changes mode.</summary>
    public async Task ClearResultAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            _lastResult = null;
            _lastError = null;
            _statistics = RunStatistics.Empty;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Makes a recipe the one single inspections (the operator's, or a trigger from outside) are
    /// judged against, without starting the continuous loop. Reloaded from disk every time, so
    /// edits saved since are picked up. A running loop owns its recipe and is left alone.
    /// </summary>
    public async Task SetActiveRecipeAsync(string? recipeId)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (IsRunning)
            {
                return;
            }

            Recipe? loaded = null;
            if (recipeId is not null)
            {
                try
                {
                    loaded = await recipes.GetAsync(recipeId);
                }
                catch (ArgumentException)
                {
                    // An id that could not be a recipe file name is simply no recipe.
                }
            }

            // A different recipe starts its own tally.
            if (_recipe?.Id != loaded?.Id)
            {
                _lastResult = null;
                _lastError = null;
                _statistics = RunStatistics.Empty;
            }

            _recipe = loaded;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// One inspection: fire the trigger, judge that frame, count it. This is what the operator's
    /// "single inspection" and a trigger arriving over TCP/IP both do.
    /// </summary>
    /// <exception cref="InspectionBusyException">The continuous loop, or another single inspection, is already using the camera.</exception>
    /// <exception cref="CameraConfigurationException">There is nothing to inspect with: no recipe, or no camera.</exception>
    public async Task<InspectionResult> RunOnceAsync(string? recipeId = null, CancellationToken ct = default)
    {
        if (IsRunning)
        {
            throw new InspectionBusyException("Sürekli denetim çalışırken tek denetim yapılamaz.");
        }

        if (!await _cycleLock.WaitAsync(0, ct))
        {
            throw new InspectionBusyException("Bir denetim zaten sürüyor.");
        }

        try
        {
            await SetActiveRecipeAsync(recipeId ?? _recipe?.Id);
            var recipe = _recipe ?? throw new CameraConfigurationException("Denetim için etkin bir reçete yok.");

            if (!camera.IsConnected)
            {
                throw new CameraConfigurationException("Denetim için kamera bağlı olmalı.");
            }

            activity.ReportStarted();
            try
            {
                var result = await CycleAsync(recipe, DateTime.UtcNow, ct);
                activity.ReportCompleted(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                activity.ReportFailed(ex.Message);
                throw;
            }
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    /// <summary>The shared body of one inspection, for the loop and for single inspections alike.</summary>
    private async Task<InspectionResult> CycleAsync(Recipe recipe, DateTime startedAt, CancellationToken ct)
    {
        var settings = await camera.GetSettingsAsync();

        // In trigger mode the camera is idle until asked, so the cycle has to fire the trigger;
        // free-running, it judges the newest frame that already arrived.
        var frame = settings.TriggerMode == "On"
            ? await camera.TriggerAsync(ct)
            : frames.Require();

        var result = engine.Run(recipe, frame);
        var elapsed = (DateTime.UtcNow - startedAt).TotalMilliseconds;

        _lastResult = result;
        _lastError = null;
        _statistics = _statistics.Add(result.Passed, elapsed);

        await ArchiveAsync(frame, recipe, result);
        return result;
    }

    private async Task StopCoreAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync();
        try
        {
            if (_loop is not null)
            {
                await _loop;
            }
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
        _cancellation = null;
        _loop = null;
    }

    private async Task RunLoopAsync(Recipe recipe, CancellationToken ct)
    {
        // Yield first so StartAsync returns before the first cycle runs and the caller sees the
        // state as running rather than as whatever the first inspection happens to take.
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            activity.ReportStarted();
            try
            {
                var result = await CycleAsync(recipe, DateTime.UtcNow, ct);
                activity.ReportCompleted(result);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad cycle — a dropped frame, a camera hiccup — must not end the run; the line
                // is still going. The error is surfaced and the loop tries again.
                _lastError = ex.Message;
                activity.ReportFailed(ex.Message);
            }

            try
            {
                await Task.Delay(IdleBetweenCycles, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ArchiveAsync(CameraFrame frame, Recipe recipe, InspectionResult result)
    {
        if (!images.ShouldSave(result.Passed))
        {
            return;
        }

        try
        {
            await images.SaveAsync(frame, new SavedImageContext(
                Verdict: result.Passed ? "OK" : "NG",
                RecipeId: recipe.Id,
                RecipeName: recipe.Name));
        }
        catch (Exception ex)
        {
            // A full or unwritable image folder must not stop the line.
            Console.WriteLine($"[Run] Görüntü kaydedilemedi: {ex.Message}");
        }
    }

    /// <summary>
    /// Shutting down is not the operator stopping the line, so the recorded run is left in place:
    /// clearing it here meant a service restart could never resume what it was doing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _stateLock.Release();
        }

        _stateLock.Dispose();
        _cycleLock.Dispose();
    }
}

/// <summary>Something else is already inspecting; a trigger that arrives now is refused, not queued.</summary>
public sealed class InspectionBusyException(string message) : Exception(message);

public sealed record RunState(
    bool IsRunning,
    string? RecipeId,
    string? RecipeName,
    InspectionResult? LastResult,
    /// <summary>The most recent cycle's failure, if the loop is limping rather than stopped.</summary>
    string? LastError,
    RunStatistics Statistics);

public sealed record RunStatistics(
    long Total,
    long Ok,
    long Ng,
    double LastMs,
    double MinMs,
    double MaxMs,
    double SumMs)
{
    public static readonly RunStatistics Empty = new(0, 0, 0, 0, 0, 0, 0);

    public RunStatistics Add(bool passed, double elapsedMs) => new(
        Total + 1,
        Ok + (passed ? 1 : 0),
        Ng + (passed ? 0 : 1),
        elapsedMs,
        // The first cycle is both the fastest and the slowest so far.
        Total == 0 ? elapsedMs : Math.Min(MinMs, elapsedMs),
        Math.Max(MaxMs, elapsedMs),
        SumMs + elapsedMs);
}
