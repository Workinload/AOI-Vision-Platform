using AOI.Contracts;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class A0SimTimingContractTests
{
    [Test]
    public void PerWorkpieceReadOnlyMilestones_PreserveActualObservedIdentity()
    {
        var id = Guid.NewGuid();
        var trace = Guid.NewGuid();
        var op = Guid.NewGuid();
        var reply = new SimTimingReply(true, "SIM_ONLY_READ_ONLY", id, true, false,
            [new SimTimingMilestone(1, "master", "DIRECT_OBSERVATION", "CamDoneAcknowledged",
                DateTimeOffset.UtcNow, id, trace, 1, "SIMREL-A", Guid.NewGuid(),
                782L, Guid.NewGuid(), "HIK-M-2", "CH06", 1,
                Guid.NewGuid(), op, null, "D1011", 2, 42, "ACTUAL_FINS_UDP_ACK")],
            []);
        Assert.DoesNotThrow(reply.Validate);
        var decoded = WireFormat.Parse<SimTimingReply>(WireFormat.Serialize(reply));
        Assert.That(decoded.Events.Single().OperationId, Is.EqualTo(op));
        Assert.That(decoded.Events.Single().FinsSid, Is.EqualTo(42));
        Assert.That(decoded.Events.Single().PlcValue, Is.EqualTo(2));
    }

    [Test]
    public void CannotRelabelAnotherWorkpiecesEvidenceOrExposeUnboundedHistory()
    {
        var id = Guid.NewGuid();
        var row = new SimTimingMilestone(1, "slave", "DIRECT_OBSERVATION", "FrameReady",
            DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), 1, "SIMREL-A",
            Guid.NewGuid(), 15L, Guid.NewGuid(), "HIK-M-2", "CH06", 1,
            Guid.NewGuid(), null, null, null, null, null, "PIXELS_VERIFIED");
        Assert.Throws<InvalidDataException>(() =>
            new SimTimingReply(true, "SIM_ONLY_READ_ONLY", id, true, false, [row], []).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new SimTimingReply(true, "SIM_ONLY_READ_ONLY", id, true, false,
                Enumerable.Repeat(row with { WorkpieceId = id }, 121).ToArray(), []).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new SimTimingReply(true, "SIM_ONLY_READ_ONLY", id, false, false, [], []).Validate());
    }
}
