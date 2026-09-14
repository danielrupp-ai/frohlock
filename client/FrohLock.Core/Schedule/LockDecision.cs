namespace FrohLock.Core.Schedule;

public enum LockState { Unlocked, Locked }

public sealed record LockDecision(LockState State, string Reason)
{
    public bool IsLocked => State == LockState.Locked;
    public static LockDecision Locked(string reason) => new(LockState.Locked, reason);
    public static LockDecision Unlocked(string reason) => new(LockState.Unlocked, reason);
}
