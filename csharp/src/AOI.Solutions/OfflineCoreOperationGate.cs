using AOI.Domain;

namespace AOI.Solutions;

/// <summary>
/// Local SIM_ONLY mutual exclusion between the ACTUAL Master/Slave Core lifetimes
/// and any offline A6 publish/changeover editor. The lock files are not evidence
/// journals and are NEVER deleted on crash/restart: the OS releases file handles.
/// It is not a remote-machine lock or a license for a real PLC production write.
/// </summary>
public static class OfflineCoreOperationGate
{
    private const string FileName = ".aoi-core-running.lease";

    /// <summary>
    /// Hold from before a Core opens production/A6 SQLite until that Core exits.
    /// Do NOT replace with a one-time TCP or process-name check: those race with
    /// an offline changeover between its safety check and its SQLite commit.
    /// </summary>
    public static IDisposable HoldCoreForLifetime(string dataRoot, MachineRole role)
        => OpenExclusive(dataRoot, role);

    /// <summary>
    /// Atomically holds both node leases for the ENTIRE offline edit/publish/
    /// activate operation. If either Core is running or another manager holds
    /// the pair, fail closed and release the first lease.
    /// </summary>
    public static OfflineLease AcquireStoppedCores(string dataRoot)
    {
        FileStream? master = null;
        FileStream? slave = null;
        try
        {
            master = OpenExclusive(dataRoot, MachineRole.Master);
            slave = OpenExclusive(dataRoot, MachineRole.Slave);
            return new OfflineLease(Path.GetFullPath(dataRoot), master, slave);
        }
        catch (IOException ex)
        {
            slave?.Dispose();
            master?.Dispose();
            throw new InvalidOperationException(
                "OFFLINE_CHANGEOVER_BLOCKED: Core is running or its exclusive lifetime lease cannot be obtained.", ex);
        }
        catch
        {
            slave?.Dispose();
            master?.Dispose();
            throw;
        }
    }

    private static FileStream OpenExclusive(string dataRoot, MachineRole role)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("A real, explicit local data root is required.", nameof(dataRoot));
        if (role is not (MachineRole.Master or MachineRole.Slave))
            throw new ArgumentOutOfRangeException(nameof(role), "Only actual Master/Slave Cores may hold an A6 lifetime lease.");
        string folder = Path.Combine(Path.GetFullPath(dataRoot),
            role == MachineRole.Master ? "master" : "slave");
        Directory.CreateDirectory(folder);
        // FileShare.None is enforced by .NET across processes on Windows/Linux.
        // Keep the returned file handle OPEN; the mere existence of this file
        // does not indicate a Core is live after a crash.
        return new FileStream(Path.Combine(folder, FileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public sealed class OfflineLease : IDisposable
    {
        private readonly string _root;
        private readonly FileStream _master;
        private readonly FileStream _slave;
        private bool _disposed;

        internal OfflineLease(string root, FileStream master, FileStream slave)
        {
            _root = root;
            _master = master;
            _slave = slave;
        }

        /// <summary>
        /// A new activation MUST use a clean run root. Orphaned -wal/-shm or
        /// final output forensic files are evidence of an unknown prior run,
        /// never a cue to delete, repair or auto-replay irreversible PLC writes.
        /// Must be called WHILE holding both Core lifetime locks.
        /// </summary>
        public void RequireCleanActivationRoot()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OfflineLease));
            string[] names =
            [
                "production.db",
                "sim-full-cycle.db",
                "final-plc-output.db",
                "timing-events.sqlite"
            ];
            foreach (string role in new[] { "master", "slave" })
            {
                foreach (string name in names)
                {
                    foreach (string suffix in new[] { "", "-wal", "-shm" })
                    {
                        string path = Path.Combine(_root, role, name + suffix);
                        if (File.Exists(path))
                            throw new InvalidOperationException(
                                "OFFLINE_CHANGEOVER_BLOCKED_PRIOR_EVIDENCE: " + path +
                                ". Preserve/inspect the original WAL, never delete or replay it automatically.");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _slave.Dispose();
            _master.Dispose();
        }
    }
}
