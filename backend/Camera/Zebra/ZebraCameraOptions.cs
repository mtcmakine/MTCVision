namespace MTCVision.Backend.Camera.Zebra;

/// <summary>
/// Configuration for the Zebra FS40/S40 camera backend.
///
/// This scanner has no GigE Vision/GenICam license, so it cannot be reached the way the DALSA
/// camera is. Instead: MTCVision connects out to the scanner's Aurora Focus "TCP/IP Control" port
/// and sends a trigger string to fire the deployed job, and that job's own "Connect &gt; Save Image
/// &gt; FTP" output pushes the resulting .jpg back to an FTP server MTCVision runs locally. Every
/// field below has to match what was entered in Aurora Focus's own settings screens.
/// </summary>
public sealed class ZebraCameraOptions
{
    public const string SectionName = "Camera:Zebra";

    /// <summary>The scanner's IP address (the same one its Aurora Focus web HMI is reachable at).</summary>
    public string DeviceIp { get; set; } = "";

    /// <summary>Aurora Focus &gt; device Communication Settings &gt; TCP/IP Settings &gt; Control Port Number.</summary>
    public int TriggerPort { get; set; } = 6000;

    /// <summary>
    /// Must match Aurora Focus's TCP/IP Settings &gt; Trigger String, with Trigger String Type set to
    /// "Explicit" and the job's Connect &gt; Triggers &gt; Trigger Source set to "TCP/IP".
    /// </summary>
    public string TriggerString { get; set; } = "TRIGGER";

    /// <summary>Must match Aurora Focus's TCP/IP Settings &gt; Control Terminator (default there is CR+LF).</summary>
    public string TriggerTerminator { get; set; } = "\r\n";

    /// <summary>
    /// Where a triggered job's image comes from: <c>"Hmi"</c> (default) reads it from the socket.io
    /// channel the scanner's web HMI uses, needing nothing but the trigger; <c>"Ftp"</c> waits for
    /// the job's Save Image output to push it to the embedded FTP server below.
    /// </summary>
    public string ImageSource { get; set; } = "Hmi";

    /// <summary>Port of the scanner's Aurora Focus web HMI, whose socket.io channel <c>"Hmi"</c> listens on.</summary>
    public int HmiPort { get; set; } = 80;

    /// <summary>How long to wait for the triggered image to arrive before giving up.</summary>
    public int TriggerTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// FTP control port the embedded server listens on. Aurora Focus's own "Save Image &gt; FTP"
    /// setup screen has no port field, so this has to stay the standard FTP port.
    /// </summary>
    public int FtpControlPort { get; set; } = 21;

    /// <summary>Must match the Username typed into Aurora Focus's FTP settings (an anonymous login is accepted as well).</summary>
    public string FtpUsername { get; set; } = "admin";

    /// <summary>Must match the Password typed into Aurora Focus's FTP settings.</summary>
    public string FtpPassword { get; set; } = "admin";

    /// <summary>Passive-mode data port range; the Windows Firewall rule opened for the server must match this.</summary>
    public int FtpPasvMinPort { get; set; } = 60000;

    public int FtpPasvMaxPort { get; set; } = 60009;
}
