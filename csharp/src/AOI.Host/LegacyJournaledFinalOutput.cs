using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using AOI.Runtime.Plc;
using System.Net.Sockets;

namespace AOI.Host;

/// <summary>
/// A0's ONLY bridge between Agent01's durable final-output state authority
/// and Agent02's actual localhost FINS/UDP transport. Never performs a write
/// without a persisted A1 one-time claim. Never retries an uncertain write.
/// </summary>
internal sealed class LegacyJournaledFinalOutput
{
    private readonly LegacyFinsLoopbackClient _plc;
    private readonly FinalPlcOutputJournal _journal;
    private readonly HostPeerInterlock _safety;

    public LegacyJournaledFinalOutput(LegacyFinsLoopbackClient plc,
        FinalPlcOutputJournal journal, HostPeerInterlock safety)
    {
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
    }

    public async Task<LegacyOutputReceipt> WriteFinalAsync(
        ProductionStore store, WorkpieceIdentity item, ushort token,
        LegacyExitScenario scenario, FinalDisposition disposition,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(item);
        if (_safety.IsLatched())
            throw new InvalidOperationException(
                "SIM_FULL_FINAL_OUTPUT_INTERLOCKED: manual reconciliation required.");
        // A1 validates the full immutable WorkpieceId/Epoch/Release/Trace,
        // already-persisted FINAL (normal) or HOLD (abnormal) and unique token.
        var progress = _journal.Prepare(store, item, token, scenario, disposition);
        if (progress.Complete || progress.RequiresManualReconciliation)
            throw new InvalidOperationException(
                "Previous final PLC intent is completed or uncertain; never replay.");
        Guid intentId = progress.Intent.IntentId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(LegacyPlcTiming.HistoricalReference.EndResultDeadline);
        var budget = deadline.Token;
        var confirmed = new List<LegacyOutputStep>(2);

        try
        {
            // D4303 is a private SIM_ONLY request for the external scanner's
            // original D2099=1/D2100 input. No production DM is written yet.
            await _plc.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
                [token, (ushort)scenario], budget);
            var scanRequest = await _plc.ReadAsync(
                LegacyPlcShadow.EndRequestAddress, 1, budget);
            var words = await _plc.ReadAsync(
                LegacyPlcShadow.EndBarcodeAddress, 10, budget);
            if (scanRequest.Length != 1 || scanRequest[0] != 1)
                throw new InvalidDataException(
                    "SIM_LEGACY_END_SCANNER_REQUEST_NOT_PRESENT");
            string scanned = A2FinsProtocol.DecodeBaAscii(words);
            bool scanValid = scenario switch
            {
                LegacyExitScenario.Normal => scanned == item.Barcode,
                LegacyExitScenario.BarcodeFailure =>
                    string.IsNullOrWhiteSpace(scanned),
                LegacyExitScenario.BarcodeMismatch =>
                    !string.IsNullOrWhiteSpace(scanned) &&
                    !string.Equals(scanned, item.Barcode, StringComparison.Ordinal),
                _ => false
            };
            if (!scanValid)
                throw new InvalidDataException(
                    "External PLC scanner input disagrees with immutable output intent.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or
                                   InvalidDataException or OperationCanceledException)
        {
            // An offer may have reached PLC while its response was lost.
            // Freeze all production rather than offering/claiming again.
            _safety.Latch("FINAL_FINS_UNCERTAIN",
                "preflight: " + ex);
            throw new LegacyOutputIncompleteException(token, confirmed.ToArray(), ex);
        }

        while (!budget.IsCancellationRequested)
        {
            var claim = _journal.ClaimNextWrite(intentId);
            if (claim is null) break;
            DateTimeOffset started = DateTimeOffset.UtcNow;
            try
            {
                var ack = await _plc.WriteObservedAsync(
                    claim.DmAddress, claim.Value,
                    beforeSend =>
                    {
                        if (!_journal.ObserveWireSend(claim.ClaimId, beforeSend))
                            throw new InvalidOperationException(
                                "FINS wire evidence already consumed; no duplicate send.");
                    }, budget);
                if (!ack.ReplyVerified || ack.AcknowledgedUtc is null ||
                    !_journal.ConfirmFinsAck(claim.ClaimId, ack))
                    throw new IOException("Unverified actual FINS reply.");
                var readback = await _plc.ReadAsync(claim.DmAddress, 1, budget);
                DateTimeOffset readbackUtc = DateTimeOffset.UtcNow;
                if (readback.Length != 1 ||
                    !_journal.ConfirmReadback(claim.ClaimId, readback[0]))
                    throw new IOException("Unverified actual FINS result readback.");
                confirmed.Add(new LegacyOutputStep(claim.DmAddress, claim.Value,
                    started, ack.AcknowledgedUtc.Value, readbackUtc, readback[0]));
            }
            catch (Exception ex) when (ex is IOException or SocketException or
                                       InvalidDataException or OperationCanceledException or
                                       InvalidOperationException)
            {
                // A1 has already fsynced the unique claim. A failed/ambiguous
                // ACK, readback or cancellation can NEVER grant a second send.
                _journal.MarkUnknown(claim.ClaimId, "FINS_ACK_OR_READBACK_UNKNOWN");
                _safety.Latch("FINAL_FINS_UNCERTAIN",
                    "token=" + token + " item=" + item.WorkpieceId +
                    " step=" + claim.StepIndex + " " + ex);
                Console.Error.WriteLine(
                    "SIM_LEGACY_FINS_OUTPUT_UNCERTAIN token=" + token +
                    " workpiece=" + item.WorkpieceId +
                    " intent=" + intentId +
                    " step=" + claim.StepIndex +
                    " acknowledged_steps=" + confirmed.Count +
                    " no_automatic_retry=1 " + ex.Message);
                throw new LegacyOutputIncompleteException(token,
                    confirmed.ToArray(), ex);
            }
        }

        var final = _journal.Get(intentId);
        if (!final.Complete || confirmed.Count != 2)
        {
            _safety.Latch("FINAL_FINS_UNCERTAIN",
                "Final UDP output ended without two confirmed steps for " + item.WorkpieceId);
            throw new LegacyOutputIncompleteException(token,
                confirmed.ToArray(), new InvalidDataException(
                    "Final PLC effect missing fully read-back verified steps."));
        }
        var result = new LegacyOutputReceipt(token, scenario, disposition,
            confirmed.ToArray(), DateTimeOffset.UtcNow, IsSimulation: true);
        Console.WriteLine("SIM_A0_JOURNALED_FINAL_OUTPUT_PASS token=" + token +
            " workpiece=" + item.WorkpieceId + " intent=" + intentId +
            " A1_steps=" + final.ConfirmedSteps + " no_replay=1");
        if (scenario == LegacyExitScenario.Normal)
            Console.WriteLine("SIM_LEGACY_D1016_D2099_FINS_PASS token=" + token +
                " disposition=" + disposition +
                " steps=" + string.Join(",", confirmed.Select(x =>
                    "D" + x.DmAddress + "=" + x.Value)));
        else
            Console.WriteLine("SIM_LEGACY_ABNORMAL_FINS_PASS token=" + token +
                " scenario=" + scenario +
                " steps=" + string.Join(",", confirmed.Select(x =>
                    "D" + x.DmAddress + "=" + x.Value)) +
                " physical_exit=HOLD");
        return result;
    }
}
