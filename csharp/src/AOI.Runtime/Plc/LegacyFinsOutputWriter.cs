using AOI.Domain;
using System.Net.Sockets;

namespace AOI.Runtime.Plc;

/// <summary>Actual FINS datagram evidence for one successful SIM_ONLY output write/readback.</summary>
public sealed record LegacyOutputStep(
    ushort DmAddress, ushort Value,
    DateTimeOffset RequestUtc, DateTimeOffset AckUtc, DateTimeOffset ReadbackUtc,
    ushort ConfirmedValue);

public sealed record LegacyOutputReceipt(
    ushort Token, LegacyExitScenario Scenario, FinalDisposition Disposition,
    IReadOnlyList<LegacyOutputStep> ConfirmedSteps,
    DateTimeOffset FinishedUtc, bool IsSimulation);

/// <summary>
/// An output may have been applied when its reply was lost. Never silently retry
/// either D1016 or D2099 after this exception; preserve ConfirmedSteps in WAL.
/// </summary>
public sealed class LegacyOutputIncompleteException : IOException
{
    public ushort Token { get; }
    public IReadOnlyList<LegacyOutputStep> ConfirmedSteps { get; }

    public LegacyOutputIncompleteException(ushort token,
        IReadOnlyList<LegacyOutputStep> confirmedSteps, Exception inner)
        : base("SIM_LEGACY_OUTPUT_INCOMPLETE: manual reconciliation of wire evidence required.", inner)
    {
        Token = token;
        ConfirmedSteps = confirmedSteps;
    }
}

/// <summary>
/// Callable from A0's Master finalization AFTER durable quality and barcode proof.
/// Performs the historical branch-specific FINS writes and per-step readbacks over
/// the same strict IPv4-loopback client. It never targets real equipment.
/// </summary>
public static class LegacyFinsOutputWriter
{
    public static async Task<LegacyOutputReceipt> WriteFinalAsync(
        LegacyFinsLoopbackClient client, ushort token, LegacyExitScenario scenario,
        FinalDisposition disposition, TimeSpan overallDeadline,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (token == 0 || !Enum.IsDefined(scenario) ||
            disposition is not (FinalDisposition.Good or FinalDisposition.Reject) ||
            (scenario != LegacyExitScenario.Normal && disposition != FinalDisposition.Reject) ||
            overallDeadline <= TimeSpan.Zero || overallDeadline > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Invalid SIM_ONLY final scenario/quality/token/budget.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(overallDeadline);
        var confirmed = new List<LegacyOutputStep>(2);
        try
        {
            var ready = await client.ReadAsync(LegacyPlcShadow.EndRequestAddress, 1,
                budget.Token);
            if (ready[0] != 1)
                throw new InvalidDataException("SIM_LEGACY_END_SCANNER_REQUEST_NOT_PRESENT");

            ushort quality = disposition == FinalDisposition.Good ? (ushort)1 : (ushort)2;
            var expected = scenario switch
            {
                LegacyExitScenario.Normal =>
                    new (ushort Address, ushort Value)[]
                    {
                        (LegacyPlcShadow.FinalQualityAddress, quality),
                        (LegacyPlcShadow.EndRequestAddress, 2)
                    },
                LegacyExitScenario.BarcodeFailure =>
                    new (ushort Address, ushort Value)[]
                    {
                        (LegacyPlcShadow.EndRequestAddress, 3),
                        (LegacyPlcShadow.FinalQualityAddress, 2)
                    },
                LegacyExitScenario.BarcodeMismatch =>
                    new (ushort Address, ushort Value)[]
                    {
                        (LegacyPlcShadow.EndRequestAddress, 4),
                        (LegacyPlcShadow.FinalQualityAddress, 2)
                    },
                _ => throw new InvalidDataException("Unknown legacy exit scenario.")
            };
            foreach (var (address, value) in expected)
            {
                DateTimeOffset started = DateTimeOffset.UtcNow;
                await client.WriteAsync(address, [value], budget.Token);
                DateTimeOffset acked = DateTimeOffset.UtcNow;
                var readback = await client.ReadAsync(address, 1, budget.Token);
                DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
                if (readback.Length != 1 || readback[0] != value)
                    throw new InvalidDataException($"Legacy FINS D{address} readback mismatch.");
                confirmed.Add(new LegacyOutputStep(address, value, started,
                    acked, checkedAt, readback[0]));
            }
            return new LegacyOutputReceipt(token, scenario, disposition,
                confirmed.ToArray(), DateTimeOffset.UtcNow, IsSimulation: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
                                   SocketException or OperationCanceledException)
        {
            throw new LegacyOutputIncompleteException(token, confirmed.ToArray(), ex);
        }
    }
}
