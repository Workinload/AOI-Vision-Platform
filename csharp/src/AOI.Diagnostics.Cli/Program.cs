using AOI.Diagnostics;

static int Usage()
{
    Console.Error.WriteLine("SIM_ONLY / READ_ONLY. Usage: AOI.Diagnostics.Cli [capture ROOT JOURNAL | watch ROOT JOURNAL [seconds] | replay JOURNAL | bundle JOURNAL OUTPUT.zip | verify OUTPUT.zip]");
    return 2;
}
try
{
    if (args.Length < 2) return Usage();
    const int staleSeconds = 30;
    switch (args[0])
    {
        case "capture" when args.Length == 3:
        {
            var entry = FlightRecorder.Append(args[2], ReadOnlyCapture.Capture(args[1]));
            Console.WriteLine($"SIM_ONLY_CAPTURE seq={entry.Sequence} sha256={entry.EntrySha256}");
            return 0;
        }
        case "watch" when args.Length is 3 or 4:
        {
            int seconds = args.Length == 4 ? int.Parse(args[3]) : 5;
            if (seconds < 1 || seconds > 3600) return Usage();
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            string? lastState = null;
            while (!cancel.IsCancellationRequested)
            {
                var snapshot = ReadOnlyCapture.Capture(args[1]);
                var hash = DiagnosticsFormat.StateHash(snapshot);
                // Always retain the last real sample, even if the business state does not change,
                // so time-based liveness can be replayed and audited.
                var entry = FlightRecorder.Append(args[2], snapshot);
                if (lastState != hash)
                {
                    Console.WriteLine($"SIM_ONLY_STATE_CHANGED seq={entry.Sequence} hash={hash}");
                    lastState = hash;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancel.Token); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            }
            return 0;
        }
        case "replay" when args.Length == 2:
        {
            var assessments = DiagnosticAnalyzer.Replay(args[1], TimeSpan.FromSeconds(staleSeconds));
            Console.WriteLine(DiagnosticsFormat.Serialize(assessments));
            return assessments.Any(a => a.Findings.Any(f => f.Severity is "ERROR" or "CRITICAL")) ? 3 : 0;
        }
        case "bundle" when args.Length == 3:
            DiagnosticBundle.Export(args[1], args[2], TimeSpan.FromSeconds(staleSeconds));
            Console.WriteLine("SIM_ONLY_BUNDLE_CREATED " + Path.GetFullPath(args[2]));
            return 0;
        case "verify" when args.Length == 2:
            DiagnosticBundle.Verify(args[1]);
            Console.WriteLine("SIM_ONLY_BUNDLE_VERIFIED");
            return 0;
        default: return Usage();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"DIAGNOSTICS_FAILED {ex.GetType().Name}: {ex.Message}");
    return 1;
}
