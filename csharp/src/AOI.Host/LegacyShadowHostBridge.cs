using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Plc;
using System.Text.Json;

namespace AOI.Host;

/// <summary>
/// Opt-in, localhost-only bridge between the existing five-process SIM_FULL host
/// and the old PLC's observable barcode and final-result FINS handshakes.
/// The legacy FIFO profile remains the default regression mode.
/// This is NOT a real PLC transport or a claim of field-verified deadlines.
/// </summary>
internal static class LegacyShadowHostBridge
{
    public static bool Enabled => Environment.GetEnvironmentVariable("AOI_SIM_PLC_PROFILE") switch
    {
        null or "" or "FIFO_DEMO" => false,
        "LEGACY_PLC_SHADOW" => true,
        _ => throw new InvalidOperationException("Unsupported SIM_ONLY PLC profile.")
    };

    public static LegacyFinsLoopbackClient NewClient() =>
        new(FinsDm.SimPort, LegacyFinsNodes.Demo);

    /// <summary>
    /// Shadow replaces the vPLC UDP server on the SAME loopback port, wrapping
    /// its FIFO registers rather than running two conflicting PLC services.
    /// Raw FINS requests/replies are periodically flushed for independent QA.
    /// </summary>
    public static async Task RunPlcAsync(SimFullVirtualPlc fifo, string dataRoot,
        CancellationToken ct)
    {
        var shadow = new LegacyPlcShadow(fifo, LegacyFinsNodes.Demo);
        using var logging = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var export = ExportWireAsync(shadow, dataRoot, logging.Token);
        try
        {
            await new LegacyPlcShadowUdp(shadow, FinsDm.SimPort).RunAsync(ct);
        }
        finally
        {
            logging.Cancel();
            await export;
        }
    }

    private static async Task ExportWireAsync(LegacyPlcShadow shadow, string dataRoot,
        CancellationToken ct)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "p0-fins-wire.jsonl");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 65536, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream);
        int written = 0;
        async Task FlushNewAsync()
        {
            var entries = shadow.WireHistory;
            for (int i = written; i < entries.Count; i++)
                await writer.WriteLineAsync(JsonSerializer.Serialize(entries[i]));
            written = entries.Count;
            await writer.FlushAsync();
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await FlushNewAsync();
                await Task.Delay(60, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await FlushNewAsync(); }
    }

    /// <summary>
    /// D4302 is an explicitly private simulator input, never an OEM PLC address.
    /// It must precede D4001 so the original FIFO still presents this token's
    /// D2000 barcode; D1999 itself is acknowledged through actual UDP writes.
    /// </summary>
    public static Task OfferLoadAsync(LegacyFinsLoopbackClient client,
        ushort token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.WriteAsync(LegacyPlcShadow.SimOfferLoadAddress, [token], ct);
    }

    public static async Task ConfirmLoadAsync(LegacyFinsLoopbackClient client,
        ushort token, string expectedBarcode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var request = await client.ReadAsync(LegacyPlcShadow.LoadRequestAddress, 1, ct);
        if (request.Length != 1 || request[0] != 1)
            throw new InvalidDataException("SIM_LEGACY_LOAD_REQUEST_MISSING");
        var words = await client.ReadAsync(LegacyPlcShadow.LoadBarcodeAddress, 10, ct);
        string observed = A2FinsProtocol.DecodeBaAscii(words);
        if (!string.Equals(observed, expectedBarcode, StringComparison.Ordinal))
            throw new InvalidDataException("SIM_LEGACY_LOAD_BARCODE_MISMATCH");
        await client.WriteAsync(LegacyPlcShadow.LoadRequestAddress, [2], ct);
        var receipt = await client.ReadAsync(LegacyPlcShadow.LoadRequestAddress, 1, ct);
        if (receipt.Length != 1 || receipt[0] != 2)
            throw new InvalidDataException("SIM_LEGACY_LOAD_ACK_READBACK_MISSING");
        Console.WriteLine("SIM_LEGACY_D1999_FINS_ACK token=" + token + " barcode=" + observed);
    }

    /// <summary>
    /// Called only after Master persisted an immutable, complete final verdict.
    /// Performs TWO individual old-address FINS writes with per-write readback.
    /// An incomplete receipt must not be retried or silently treated as success.
    /// </summary>
    /// <summary>
    /// OEM legacy abnormal end-scan order (opposite to normal quality-first):
    /// D2099=3 on unreadable barcode or D2099=4 on a different barcode,
    /// followed by D1016=2. The initial D4303 is PRIVATE simulator input;
    /// the two legacy register writes are distinct real loopback FINS datagrams
    /// and require separate ACK/readback evidence.
    /// A HOLD must already be durably committed BEFORE entering this method.
    /// </summary>
    public static async Task<LegacyOutputReceipt> WriteAbnormalFinalAsync(
        LegacyFinsLoopbackClient client, ushort token, LegacyExitScenario scenario,
        string expectedBarcode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (token == 0 || scenario is not
            (LegacyExitScenario.BarcodeFailure or LegacyExitScenario.BarcodeMismatch) ||
            string.IsNullOrWhiteSpace(expectedBarcode))
            throw new ArgumentException("Invalid legacy end-scanner failure context.");
        await client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
            [token, (ushort)scenario], ct);
        // Verify scanner data is actually exposed by the external PLC shadow.
        // A Host-only simulated flag must never fake this input observation.
        var scan = await client.ReadAsync(LegacyPlcShadow.EndRequestAddress, 1, ct);
        var words = await client.ReadAsync(LegacyPlcShadow.EndBarcodeAddress, 10, ct);
        if (scan.Length != 1 || scan[0] != 1)
            throw new InvalidDataException("Legacy end scanner request not present.");
        string observed = A2FinsProtocol.DecodeBaAscii(words);
        if (scenario == LegacyExitScenario.BarcodeFailure
            ? !string.IsNullOrWhiteSpace(observed)
            : string.IsNullOrWhiteSpace(observed) ||
              string.Equals(observed, expectedBarcode, StringComparison.Ordinal))
            throw new InvalidDataException("Legacy end scanner failure profile does not match PLC input.");
        var receipt = await LegacyFinsOutputWriter.WriteFinalAsync(client, token,
            scenario, FinalDisposition.Reject,
            LegacyPlcTiming.HistoricalReference.EndResultDeadline, ct);
        ushort first = scenario == LegacyExitScenario.BarcodeFailure
            ? (ushort)3 : (ushort)4;
        if (receipt.ConfirmedSteps.Count != 2 ||
            receipt.ConfirmedSteps[0].DmAddress != LegacyPlcShadow.EndRequestAddress ||
            receipt.ConfirmedSteps[0].Value != first ||
            receipt.ConfirmedSteps[1].DmAddress != LegacyPlcShadow.FinalQualityAddress ||
            receipt.ConfirmedSteps[1].Value != 2)
            throw new InvalidDataException("SIM_LEGACY_ABNORMAL_FINS_ORDER_MISMATCH");
        Console.WriteLine("SIM_LEGACY_ABNORMAL_FINS_PASS token=" + token +
            " scenario=" + scenario +
            " steps=" + string.Join(",", receipt.ConfirmedSteps.Select(
                step => "D" + step.DmAddress + "=" + step.Value)) +
            " physical_exit=HOLD");
        return receipt;
    }

    public static async Task<LegacyOutputReceipt> WriteNormalFinalAsync(
        LegacyFinsLoopbackClient client, ushort token, FinalDisposition disposition,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        await client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
            [token, (ushort)LegacyExitScenario.Normal], ct);
        var receipt = await LegacyFinsOutputWriter.WriteFinalAsync(
            client, token, LegacyExitScenario.Normal, disposition,
            LegacyPlcTiming.HistoricalReference.EndResultDeadline, ct);
        if (receipt.ConfirmedSteps.Count != 2)
            throw new InvalidDataException("SIM_LEGACY_FINAL_REQUIRES_TWO_FINS_WRITES");
        Console.WriteLine("SIM_LEGACY_D1016_D2099_FINS_PASS token=" + token +
            " disposition=" + disposition +
            " steps=" + string.Join(",", receipt.ConfirmedSteps.Select(
                s => "D" + s.DmAddress + "=" + s.Value)));
        return receipt;
    }
}
