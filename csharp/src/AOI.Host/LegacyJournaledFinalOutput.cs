using AOI.Domain;
using AOI.Diagnostics;
using AOI.Runtime;
using System.Collections.Immutable;
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
    private readonly FinalOutputAuditJournal _audit;

    public LegacyJournaledFinalOutput(LegacyFinsLoopbackClient plc,
        FinalPlcOutputJournal journal, HostPeerInterlock safety,
        FinalOutputAuditJournal audit)
    {
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
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
        // A7 is evidence, NEVER output authority. A1 has already durably
        // reserved this WorkpieceId. Each actual A2 packet is subsequently
        // linked to the SAME A1 ClaimId, SID and raw FINS byte hashes.
        Guid auditAttempt;
        try
        {
            var auditScenario = scenario switch
            {
                LegacyExitScenario.Normal => FinalOutputAuditScenario.Normal,
                LegacyExitScenario.BarcodeFailure => FinalOutputAuditScenario.BarcodeFailure,
                LegacyExitScenario.BarcodeMismatch => FinalOutputAuditScenario.BarcodeMismatch,
                _ => throw new InvalidDataException("Unknown final audit scenario.")
            };
            auditAttempt = _audit.Begin(item, token, auditScenario, disposition);
        }
        catch (Exception ex)
        {
            _safety.Latch("FINAL_FINS_UNCERTAIN",
                "A7 audit unavailable before final PLC output: " + ex);
            throw new LegacyOutputIncompleteException(token, [], ex);
        }
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
            // A7 accepts only the actual on-wire scanner request/reply.
            // An invented or merely expected D2099=1 can never be recorded.
            var scanRequest = await _plc.ReadRawObservedAsync(
                LegacyPlcShadow.EndRequestAddress, 1, Guid.NewGuid(), evt =>
                {
                    if (evt.Stage == LegacyFinsObservationStage.ReadbackVerified)
                        _audit.ScannerRequestVerified(auditAttempt, evt.OperationId,
                            evt.RequestBytes.AsSpan(), evt.ResponseBytes.AsSpan());
                }, budget);
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
            if (_safety.IsLatched())
                throw new InvalidOperationException(
                    "Hardware output interlocked before issuing next FINS claim.");
            var claim = _journal.ClaimNextWrite(intentId);
            if (claim is null) break;
            DateTimeOffset started = DateTimeOffset.UtcNow;
            int stepNumber = claim.StepIndex + 1;
            bool auditPrepared = false;
            bool auditAckAccepted = false;
            bool auditTerminalReply = false;
            bool auditReadbackRecorded = false;
            try
            {
                var ack = await _plc.WriteRawObservedAsync(
                    claim.DmAddress, claim.Value, claim.ClaimId,
                    evt =>
                    {
                        if (evt.Stage == LegacyFinsObservationStage.Prepared)
                        {
                            var beforeSend = new LegacyFinsWriteProof(
                                claim.ClaimId, evt.Sid, claim.DmAddress, claim.Value,
                                evt.OccurredUtc, AcknowledgedUtc: null,
                                ReplyVerified: false);
                            // A1's exclusive persisted write entitlement owns
                            // the physical send. A7 must additionally capture
                            // the exact outgoing UDP bytes before any SendAsync.
                            if (!_journal.ObserveWireSend(claim.ClaimId, beforeSend))
                                throw new InvalidOperationException(
                                    "A1 FINS ClaimId was already consumed.");
                            _audit.PrepareStep(auditAttempt, stepNumber,
                                evt.OperationId, evt.RequestBytes.AsSpan());
                            auditPrepared = true;
                        }
                        else if (evt.Stage == LegacyFinsObservationStage.WireSent)
                        {
                            _audit.WireSent(auditAttempt, stepNumber, evt.OperationId);
                        }
                        else if (evt.Stage is LegacyFinsObservationStage.AckAccepted or
                                 LegacyFinsObservationStage.AckRejected)
                        {
                            _audit.RecordWriteReply(auditAttempt, stepNumber,
                                evt.OperationId, evt.RequestBytes.AsSpan(),
                                evt.ResponseBytes.AsSpan());
                            auditTerminalReply =
                                evt.Stage == LegacyFinsObservationStage.AckRejected;
                            auditAckAccepted =
                                evt.Stage == LegacyFinsObservationStage.AckAccepted;
                        }
                    }, budget);
                if (!ack.ReplyVerified || ack.AcknowledgedUtc is null ||
                    !_journal.ConfirmFinsAck(claim.ClaimId, ack))
                    throw new IOException("Unverified actual FINS reply.");
                var readback = await _plc.ReadRawObservedAsync(
                    claim.DmAddress, 1, Guid.NewGuid(), evt =>
                    {
                        if (evt.Stage == LegacyFinsObservationStage.ReadbackVerified)
                        {
                            _audit.RecordReadback(auditAttempt, stepNumber,
                                evt.OperationId, evt.RequestBytes.AsSpan(),
                                evt.ResponseBytes.AsSpan());
                            auditReadbackRecorded = true;
                        }
                    }, budget);
                DateTimeOffset readbackUtc = DateTimeOffset.UtcNow;
                if (readback.Length != 1 ||
                    !_journal.ConfirmReadback(claim.ClaimId, readback[0]))
                    throw new IOException("Unverified actual FINS result readback.");
                if (_safety.IsLatched())
                    throw new IOException("Peer/PLC safety latch changed during FINS output.");
                confirmed.Add(new LegacyOutputStep(claim.DmAddress, claim.Value,
                    started, ack.AcknowledgedUtc.Value, readbackUtc, readback[0]));
            }
            catch (Exception ex) when (ex is IOException or SocketException or
                                       InvalidDataException or OperationCanceledException or
                                       InvalidOperationException)
            {
                // The A1 ClaimId is fsynced BEFORE actual UDP SendAsync.
                // First freeze ALL further output, then persist A1 UNKNOWN.
                // A7 audits uncertainty but can never grant replay rights.
                _safety.Latch("FINAL_FINS_UNCERTAIN",
                    "token=" + token + " item=" + item.WorkpieceId +
                    " step=" + claim.StepIndex + " " + ex);
                if (auditPrepared && !auditTerminalReply && !auditReadbackRecorded)
                {
                    try
                    {
                        if (auditAckAccepted)
                            _audit.ReadbackUnknown(auditAttempt, stepNumber,
                                Guid.NewGuid(), "FINS_READBACK_NOT_PROVEN");
                        else
                            _audit.AckUnknown(auditAttempt, stepNumber,
                                claim.ClaimId, "FINS_ACK_NOT_PROVEN");
                    }
                    catch (Exception auditEx)
                    {
                        Console.Error.WriteLine("SIM_A7_OUTPUT_AUDIT_INCOMPLETE " +
                            auditEx.GetType().Name);
                    }
                }
                _journal.MarkUnknown(claim.ClaimId, "FINS_ACK_OR_READBACK_UNKNOWN");
                Console.Error.WriteLine(
                    "SIM_LEGACY_FINS_OUTPUT_UNCERTAIN token=" + token +
                    " workpiece=" + item.WorkpieceId +
                    " intent=" + intentId + " A7_attempt=" + auditAttempt +
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
