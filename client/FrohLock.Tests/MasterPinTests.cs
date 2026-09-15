using FrohLock.Core;
using FrohLock.Core.Crypto;
using Xunit;

namespace FrohLock.Tests;

public class MasterPinTests
{
    // Sicherstellen, dass die eingebetteten Master-Hashes zu den bekannten Master-PINs passen
    // (schützt vor Tippfehlern beim Einbetten).
    [Fact]
    public void Embedded_master_pins_verify()
    {
        Assert.True(Branding.IsMasterPin("FrohLock-Eltern-2531", PinHasher.Verify));  // Buchstaben-Master
        Assert.True(Branding.IsMasterPin("49258137", PinHasher.Verify));              // numerischer Master
    }

    [Fact]
    public void Wrong_master_pin_rejected()
    {
        Assert.False(Branding.IsMasterPin("falsch", PinHasher.Verify));
        Assert.False(Branding.IsMasterPin("00000000", PinHasher.Verify));
    }
}
