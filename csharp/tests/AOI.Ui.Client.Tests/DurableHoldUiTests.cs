using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class DurableHoldUiTests
{
    private static MachineSnapshot Sample(WorkpieceHoldOverview holds) =>
        new("master", MachineRole.Master, "SIM_PROCESSING", null, null,
            Array.Empty<ChannelSnapshot>(), DateTimeOffset.UtcNow, false, "SIM_FULL")
        {
            Holds = holds
        };

    [Test]
    public void ReadOnlyHoldProjectionPreservesCodeAndPhysicalIdentity()
    {
        var identity = Guid.NewGuid();
        var hold = new WorkpieceHoldRecord(identity, "END_BARCODE_MISMATCH",
            "Virtual test exit barcode differs", DateTimeOffset.UtcNow);
        var overview = new WorkpieceHoldOverview(1, new[] { hold });
        var result = MonitorProjection.FromSnapshot(Sample(overview),
            MachineRole.Master, DateTimeOffset.UtcNow);
        Assert.That(result.Holds!.ActiveCount, Is.EqualTo(1));
        Assert.That(result.Holds.Recent.Single().WorkpieceId, Is.EqualTo(identity));
        Assert.That(result.Holds.Recent.Single().ReasonCode, Is.EqualTo("END_BARCODE_MISMATCH"));
    }

    [Test]
    public void RejectsContradictoryCountersDuplicatedAndMalformedHolds()
    {
        var hold = new WorkpieceHoldRecord(Guid.NewGuid(), "BARCODE", "Mismatch", DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(new WorkpieceHoldOverview(0, new[] { hold })),
                MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(new WorkpieceHoldOverview(2, new[] { hold, hold })),
                MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(new WorkpieceHoldOverview(1, new[] {
                hold with { WorkpieceId = Guid.Empty }
            })), MachineRole.Master, DateTimeOffset.UtcNow));
    }
}
