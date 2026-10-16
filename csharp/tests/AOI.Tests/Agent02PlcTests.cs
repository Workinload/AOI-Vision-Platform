using System.Buffers.Binary;
using AOI.Runtime.Plc;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class Agent02PlcTests
{
    [Test]
    public void RegistryRetains45Signals14GatesAndNoProductionWrite()
    {
        Assert.That(A2SignalRegistry.Signals, Has.Count.EqualTo(45));
        Assert.That(A2SignalRegistry.FieldGates, Has.Count.EqualTo(14));
        Assert.That(A2SignalRegistry.FieldGates.Count(x => x.Status == "BLOCKED"), Is.EqualTo(7));
        Assert.That(A2SignalRegistry.FieldGates.Count(x => x.Status == "OPEN"), Is.EqualTo(7));
        Assert.That(A2SignalRegistry.Signals, Has.All.Matches<A2PlcSignal>(s => !s.ProductionWriteEnabled && s.Fields.Count == 24));
        Assert.That(A2SignalRegistry.FieldGates, Has.All.Matches<A2FieldGate>(g => !g.ProductionWriteAllowed));
        Assert.That(A2SignalRegistry.AtWord(1013).Select(s => s.Id),
            Is.EquivalentTo(new[] { "SIG_CAM_REAR_LINE", "SIG_CAM_REAR_LINE_CLEAR_ALIAS" }));
        Assert.That(A2SignalRegistry.AtWord(1000), Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(A2SignalRegistry.AtWord(2000), Has.Count.EqualTo(1));
    }

    [Test]
    public void RealEndpointsAndConflictedAddressesFailClosed()
    {
        var policy = new A2WritePolicy();
        Assert.That(policy.ProductionWritesEnabled, Is.False);
        Assert.Throws<UnauthorizedAccessException>(() => A2WritePolicy.DenyRealHardware());
        Assert.Throws<UnauthorizedAccessException>(() => A2WritePolicy.RequireLoopback("192.168.0.1", 19600));
        Assert.Throws<UnauthorizedAccessException>(() => A2WritePolicy.RequireLoopback("localhost", 19600));
        Assert.Throws<UnauthorizedAccessException>(() => A2WritePolicy.RequireLoopback("::1", 19600));
        Assert.That(A2WritePolicy.RequireLoopback("127.0.0.1", 19600).Port, Is.EqualTo(19600));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeVirtualWrite(1013, 1));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeVirtualWrite(1030, 2));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeVirtualWrite(2000, 1));
        Assert.DoesNotThrow(() => policy.AuthorizeVirtualWrite(1016, 1));
    }

    [Test]
    public void FinsReadWriteAndGoldenHeaderVectorsMatch()
    {
        var plc = new A2VirtualPlc("SIM-0001");
        var read = A2FinsProtocol.Create(false, 1999, new ushort[1], 0x2a);
        Assert.That(Convert.ToHexString(read), Is.EqualTo("8000020001000063002A01018207CF000001"));
        Assert.That(A2FinsProtocol.Decode(read, plc.Handle(read)), Is.EqualTo(new ushort[] { 1 }));
        var write = A2FinsProtocol.Create(true, 1999, new ushort[] { 2 }, 0x2b);
        Assert.That(Convert.ToHexString(write), Is.EqualTo("8000020001000063002B01028207CF0000010002"));
        A2FinsProtocol.Decode(write, plc.Handle(write));
        Assert.That(plc.Read(1999), Is.EqualTo(2));
        var barcode = A2FinsProtocol.Create(false, 2000, new ushort[10], 3);
        Assert.That(A2FinsProtocol.DecodeBaAscii(A2FinsProtocol.Decode(barcode, plc.Handle(barcode))),
            Is.EqualTo("SIM-0001"));
    }

    [Test]
    public void StrictDecoderRejectsWrongSidNodeEndCodeAndLength()
    {
        var plc = new A2VirtualPlc();
        var req = A2FinsProtocol.Create(false, 1999, new ushort[1], 0x3f);
        var reply = plc.Handle(req);
        foreach (int corruptIndex in new[] { 0, 4, 7, 9, 10, 11, 12, 13 })
        {
            var bad = (byte[])reply.Clone();
            bad[corruptIndex] ^= 1;
            Assert.Throws<InvalidDataException>(() => A2FinsProtocol.Decode(req, bad));
        }
        Assert.Throws<InvalidDataException>(() => A2FinsProtocol.Decode(req, reply[..^1]));
        Assert.That(plc.Handle(req[..^1]), Is.Empty);
        var writeBlocked = A2FinsProtocol.Create(true, 1013, new ushort[] { 2 }, 9);
        Assert.That(plc.Handle(writeBlocked), Is.Empty);
        Assert.That(plc.Read(1013), Is.EqualTo(0));
    }

    [Test]
    public void BadcFloatAndBaBarcodeRoundTrips()
    {
        var encoded = A2FinsProtocol.EncodeFloatBadc(1.0f);
        Assert.That(encoded, Is.EqualTo(new ushort[] { 0x803f, 0x0000 }));
        foreach (float sample in new[] { -17.125f, 0f, 123.625f, 0.001f })
            Assert.That(A2FinsProtocol.DecodeFloatBadc(A2FinsProtocol.EncodeFloatBadc(sample)), Is.EqualTo(sample));
        Assert.Throws<ArgumentOutOfRangeException>(() => A2FinsProtocol.EncodeFloatBadc(float.NaN));
        var words = A2FinsProtocol.EncodeBaAscii("AB123", 10);
        Assert.That(words[0], Is.EqualTo(0x4142));
        Assert.That(A2FinsProtocol.DecodeBaAscii(words), Is.EqualTo("AB123"));
        words[2] = 0x0d00;
        Assert.That(A2FinsProtocol.DecodeBaAscii(words), Is.EqualTo("AB12"));
    }
}
