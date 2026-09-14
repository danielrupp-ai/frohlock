using FrohLock.Core;
using FrohLock.Core.Crypto;
using Xunit;

namespace FrohLock.Tests;

public class MasterPinTests
{
    // Sicherstellen, dass der eingebettete Master-Hash zur bekannten Master-PIN passt
    // (schützt vor Tippfehlern beim Einbetten).
    [Fact]
    public void Embedded_master_pin_verifies()
    {
        Assert.True(PinHasher.Verify("FrohLock-Eltern-2531",
            Branding.MasterUnlockHash, Branding.MasterUnlockSalt, Branding.MasterUnlockIterations));
    }

    [Fact]
    public void Wrong_master_pin_rejected()
    {
        Assert.False(PinHasher.Verify("falsch",
            Branding.MasterUnlockHash, Branding.MasterUnlockSalt, Branding.MasterUnlockIterations));
    }
}
