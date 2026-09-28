using MTCVision.Backend;
using System.Text.Json.Serialization;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.AccountManagement.Anonymous;
using FubarDev.FtpServer.FileSystem.DotNet;
using MTCVision.Backend.Api;
using MTCVision.Backend.Camera;
using MTCVision.Backend.Camera.Zebra;
using MTCVision.Backend.Inspection;
using MTCVision.Backend.Integration;
using MTCVision.Backend.Recipes;
using MTCVision.Backend.Vision;

var builder = WebApplication.CreateBuilder(args);

// Lets the same binary run as a Windows service as well as from a console. Installed as a service
// the inspection keeps running across logoffs and reboots, which is what an unattended line needs;
// run from a console this call does nothing.
builder.Host.UseWindowsService(options => options.ServiceName = "MTCVision");

// As a service there is no console to read, so the log goes to a file beside the data. Without it
// a camera that will not reconnect at boot is invisible.
builder.Logging.AddProvider(new FileLoggerProvider(DataPaths.Resolve()));

const string FrontendDevCorsPolicy = "FrontendDev";
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendDevCorsPolicy, policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Enums travel as their names ("Contrast"), so recipe JSON on disk and over the wire stays
// readable and survives reordering the enum.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<DataPaths>();
builder.Services.AddSingleton<SessionStore>();

// Both camera kinds are always available and the operator picks one from the console (see
// SwitchableCameraService): a GigE Vision / GenICam camera such as the Teledyne DALSA Genie Nano,
// or a Zebra FS40/S40 running its base, unlicensed Aurora Focus firmware, which has no GenICam
// interface. Camera:Backend in appsettings only decides which one a first start begins with.
builder.Services.Configure<ZebraCameraOptions>(builder.Configuration.GetSection(ZebraCameraOptions.SectionName));
var zebraOptions = builder.Configuration.GetSection(ZebraCameraOptions.SectionName).Get<ZebraCameraOptions>()
    ?? new ZebraCameraOptions();

if (string.Equals(zebraOptions.ImageSource, "Ftp", StringComparison.OrdinalIgnoreCase))
{
    // The job's Save Image output pushes each image to an FTP server we run ourselves.
    var zebraIncomingPath = Path.Combine(DataPaths.Resolve(), "ZebraIncoming");
    Directory.CreateDirectory(zebraIncomingPath);

    builder.Services
        .AddFtpServer(ftp =>
        {
            ftp.UseDotNetFileSystem();

            // Not .EnableAnonymousAuthentication(): it only accepts the literal user "anonymous" and
            // wants an email-shaped password, but Aurora Focus sends whatever was typed into its
            // FTP settings (now a configured username/password, earlier an empty password).
            ftp.Services.AddSingleton<IMembershipProvider>(new ZebraFtpMembershipProvider(zebraOptions));
        })
        .Configure<FtpServerOptions>(opt => opt.Port = zebraOptions.FtpControlPort)
        .Configure<DotNetFileSystemOptions>(opt => opt.RootPath = zebraIncomingPath)
        .Configure<SimplePasvOptions>(opt =>
        {
            opt.PasvMinPort = zebraOptions.FtpPasvMinPort;
            opt.PasvMaxPort = zebraOptions.FtpPasvMaxPort;
        });

    builder.Services.AddSingleton<ZebraFtpImageReceiver>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ZebraFtpImageReceiver>());
    builder.Services.AddSingleton<IZebraImageSource>(sp => sp.GetRequiredService<ZebraFtpImageReceiver>());
}
else
{
    // Default: read the images off the socket.io channel the scanner's own web HMI uses — no
    // FTP server, no firewall rule, no extra job output to configure.
    builder.Services.AddSingleton<ZebraHmiImageSource>();
    builder.Services.AddSingleton<IZebraImageSource>(sp => sp.GetRequiredService<ZebraHmiImageSource>());
}

builder.Services.AddSingleton<GigeCameraService>();
builder.Services.AddSingleton<ZebraCameraService>();
builder.Services.AddSingleton<SwitchableCameraService>();
builder.Services.AddSingleton<ICameraService>(sp => sp.GetRequiredService<SwitchableCameraService>());

builder.Services.AddSingleton<LatestFrameStore>();
builder.Services.AddSingleton<LiveFrameBroadcaster>();
builder.Services.AddSingleton<AutoBrightnessTuner>();
builder.Services.AddSingleton<RecipeStore>();
builder.Services.AddSingleton<MasterImageStore>();
builder.Services.AddSingleton<PositionAdjuster>();
builder.Services.AddSingleton<ImageHistoryStore>();
builder.Services.AddSingleton<OcrEngine>();
builder.Services.AddSingleton<InspectionEngine>();
builder.Services.AddSingleton<InspectionActivity>();
builder.Services.AddSingleton<OperatingMode>();
builder.Services.AddSingleton<InspectionRunner>();
builder.Services.AddSingleton<ExternalIoSettingsStore>();
builder.Services.AddSingleton<ExternalIoLog>();
builder.Services.AddSingleton<ExternalIoServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExternalIoServer>());
builder.Services.AddHostedService<SessionRestorer>();

var app = builder.Build();

app.UseCors(FrontendDevCorsPolicy);
app.UseWebSockets();

// The built console is served from here, so a running install is one service on one URL rather
// than a server plus a separate dev server the operator has to remember to start. During
// development the Vite server is still used and reaches this one through CORS.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthEndpoints();
app.MapCameraEndpoints();
app.MapLiveStreamEndpoints();
app.MapRecipeEndpoints();
app.MapInspectionEndpoints();
app.MapImageHistoryEndpoints();
app.MapSessionEndpoints();
app.MapExternalIoEndpoints();

// Anything that is not an API route is the single-page app's own routing, so it gets index.html.
app.MapFallbackToFile("index.html");

app.Services.GetRequiredService<ICameraService>().StatusChanged += (_, message) =>
    Console.WriteLine($"[Camera:StatusChanged] {message}");

// Resolve eagerly: both subscribe to the camera's frame event, and a lazily created singleton
// would start collecting frames only once something first asked for it.
app.Services.GetRequiredService<LatestFrameStore>();
app.Services.GetRequiredService<LiveFrameBroadcaster>();

app.Run();
