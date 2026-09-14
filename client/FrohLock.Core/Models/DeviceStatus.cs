namespace FrohLock.Core.Models;

/// <summary>Status, den der Client im Heartbeat an den Server meldet.</summary>
public sealed class DeviceStatus
{
    public string DeviceId { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public long ConfigVersion { get; set; }
    public bool CurrentlyLocked { get; set; }
    public long TrustedTimeUnix { get; set; }
    public long TrustedTimeAgeSeconds { get; set; }
    public string LockReason { get; set; } = "";
    public int PinFailuresToday { get; set; }
    public long LastBootUnix { get; set; }
}
