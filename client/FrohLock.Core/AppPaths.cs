namespace FrohLock.Core;

/// <summary>
/// Zentrale, geschützte Pfade. ProgramData ist für Standardbenutzer nicht schreibbar
/// (ACLs werden vom Installer/Dienst gesetzt) -> Config/Log manipulationssicherer.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "FrohLock";
    public const string ServiceName = "FrohLockService";
    public const string PipeName = "FrohLock.Ipc";

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string ConfigEnvelopePath => Path.Combine(DataDir, "config.signed.json");
    public static string TrustedTimeStatePath => Path.Combine(DataDir, "time.state.json");
    public static string StatePath => Path.Combine(DataDir, "runtime.state.json");
    public static string AuditLogPath => Path.Combine(DataDir, "audit.log");
    public static string DeviceTokenPath => Path.Combine(DataDir, "device.token");
    public static string BootstrapPath => Path.Combine(DataDir, "bootstrap.json");

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
