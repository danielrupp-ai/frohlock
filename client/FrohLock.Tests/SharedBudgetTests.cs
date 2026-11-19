using FrohLock.Core;
using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

public class SharedBudgetTests
{
    [Fact]
    public void OthersCountOnlyForToday()
    {
        var s = new SharedUsage { Day = "2026-11-19", Minutes = 45 };
        Assert.Equal(45, SharedBudget.OthersToday(s, "2026-11-19"));
        Assert.Equal(0, SharedBudget.OthersToday(s, "2026-11-20"));   // neuer Tag -> wieder 0
        Assert.Equal(0, SharedBudget.OthersToday(null, "2026-11-19"));
    }

    [Fact]
    public void AcceptRejectsOtherDayAndOlderReplay()
    {
        var a = SharedBudget.Accept(null, "2026-11-19", 30, "Handy 30 Min", 1000, "2026-11-19");
        Assert.NotNull(a);
        Assert.Null(SharedBudget.Accept(a, "2026-11-18", 0, "", 2000, "2026-11-19"));   // falscher Tag
        Assert.Null(SharedBudget.Accept(a, "2026-11-19", 5, "", 900, "2026-11-19"));    // älter -> verwerfen
        var b = SharedBudget.Accept(a, "2026-11-19", 40, "Handy 40 Min", 1100, "2026-11-19");
        Assert.Equal(40, b!.Minutes);
        Assert.Null(SharedBudget.Accept(a, "2026-11-19", -1, "", 3000, "2026-11-19"));  // Unsinn
    }

    [Fact]
    public void BudgetLocksOnCombinedTime()
    {
        // Laptop 80 + Handy 40 = 120 von 120 -> gesperrt, Text nennt das andere Gerät.
        var d = BudgetPolicy.Apply(LockDecision.Unlocked("frei"), 120, 80 + 40, false, null, 40);
        Assert.True(d.IsLocked);
        Assert.Contains("120/120", d.Reason);
        Assert.Contains("40 min am anderen Gerät", d.Reason);
        // Laptop 80 + Handy 30 = 110 -> noch frei.
        Assert.False(BudgetPolicy.Apply(LockDecision.Unlocked("frei"), 120, 110, false, null, 30).IsLocked);
    }

    [Fact]
    public void ServerCommandType9ParsesAsSharedUsage()
    {
        // Genau so baut der Server (signing.sign_command) den Befehl.
        var json = "{\"commandId\":\"x\",\"deviceId\":\"d\",\"type\":9,\"issuedAtUnix\":5,\"expiresAtUnix\":605," +
                   "\"payload\":{\"day\":\"2026-11-19\",\"minutes\":\"30\",\"detail\":\"renat handy 30 Min\"}}";
        var cmd = Json.Deserialize<DeviceCommand>(json)!;
        Assert.Equal(CommandType.SharedUsage, cmd.Type);
        Assert.Equal("30", cmd.Payload!["minutes"]);
    }
}
