using AOI.Domain;
using AOI.Solutions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;

namespace AOI.Ui.Avalonia;

/// <summary>
/// One logical A6 channel tile. Ownership, station and physical device come from the
/// actual A0 ChannelCatalog; nothing here may invent MASTER/SLAVE ownership.
/// </summary>
public sealed record OfflineChannelTile(
    string ChannelId, string StationId, string PhysicalDeviceId, MachineRole OwnerRole)
{
    public string Owner => OwnerRole == MachineRole.Master ? "MASTER" : "SLAVE";
    public string Display => ChannelId + " · " + StationId + " · " + PhysicalDeviceId + " · " + Owner;
}

/// <summary>A product that actually exists in the attached A6 database.</summary>
public sealed record OfflineSolutionTile(string SolutionId, string DisplayName)
{
    public string Display => SolutionId + " · " + DisplayName;
}

/// <summary>One inspection plugin selection offered by the offline editor.</summary>
public sealed record OfflineAlgorithmChoice(string AlgorithmId, string DisplayLabel, bool Installed)
{
    public static OfflineAlgorithmChoice StationDefault { get; } =
        new("", "站默认（不写通道插件）", true);
}

/// <summary>
/// Real offline writer session. Write permission is not a Boolean anyone can flip: it exists
/// only while this object physically holds BOTH OS-level Core locks obtained from the merged
/// A0 #152 gate (<see cref="OfflineCoreOperationGate.AcquireStoppedCores"/>). Every writing
/// command checks <see cref="A6WritesAllowed"/>, which is derived from the held lease.
/// Read-only preview never needs the lease and stays available when arming fails.
/// </summary>
public sealed class OfflineWriteGate : IDisposable
{
    private readonly bool isolatedTest;
    private OfflineCoreOperationGate.OfflineLease? lease;

    private OfflineWriteGate(bool isolatedTest, string reason)
    {
        this.isolatedTest = isolatedTest;
        Reason = reason;
    }

    /// <summary>Normal construction: closed until <see cref="Arm"/> succeeds.</summary>
    public OfflineWriteGate() : this(false, OfflineA6Dependency.ClosedReason) { }

    /// <summary>A fresh, closed gate for each window. Never shared between Master and Slave.</summary>
    public static OfflineWriteGate Production => new();

    /// <summary>Test-only seam for an isolated temporary A6 database; never used by MainWindow.</summary>
    internal static OfflineWriteGate IsolatedTestDatabase =>
        new(true, "TEST_ONLY：隔离临时 A6 数据库；无 Core、无激活。");

    /// <summary>True only while both OS Core locks are physically held.</summary>
    public bool A6WritesAllowed => isolatedTest || lease is not null;

    public string Reason { get; private set; }

    /// <summary>Run root that owns the held lease; empty when closed.</summary>
    public string DataRoot { get; private set; } = "";

    /// <summary>
    /// Acquires and HOLDS both Core locks for the run root, then verifies the activation root is
    /// clean. Throws if a Core is running or if prior evidence is unusable; in that case nothing
    /// is held and writes stay closed.
    /// </summary>
    /// <summary>
    /// True only when the A6 database belongs to the run root this gate physically holds.
    /// A lease for root A must never authorise writes against root B.
    /// </summary>
    public bool CoversDatabase(string databasePath)
    {
        if (isolatedTest) return true;
        if (lease is null || string.IsNullOrWhiteSpace(databasePath)) return false;
        string? dbRoot = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (string.IsNullOrEmpty(dbRoot)) return false;
        // A reparse/symlink alias could resolve to a different physical store than the one that
        // was locked, so it is rejected instead of silently authorising writes.
        if (IsReparsePoint(dbRoot) || IsReparsePoint(DataRoot)) return false;
        // Case-insensitive only where the filesystem actually is: on Linux runA and RUNa are
        // two different physical roots, so an OrdinalIgnoreCase match there would be fail-open.
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Canonical(DataRoot), Canonical(databasePath is null ? "" : dbRoot), comparison);
    }

    public void Arm(string dataRoot)
    {
        if (isolatedTest) return;
        // Re-arming for a different root must release the previous handles first.
        Release();
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        OfflineCoreOperationGate.OfflineLease acquired =
            OfflineCoreOperationGate.AcquireStoppedCores(dataRoot);
        try
        {
            acquired.RequireCleanActivationRoot();
        }
        catch
        {
            acquired.Dispose();
            throw;
        }
        lease = acquired;
        DataRoot = dataRoot;
        Reason = "已持有双 Core 独占锁（OS 级文件句柄）：" + dataRoot;
    }

    /// <summary>Releases both handles. Safe to call repeatedly (window close, detach, failure).</summary>
    public void Release()
    {
        OfflineCoreOperationGate.OfflineLease? held = lease;
        lease = null;
        DataRoot = "";
        Reason = isolatedTest ? Reason : OfflineA6Dependency.ClosedReason;
        held?.Dispose();
    }

    private static string Canonical(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return false;
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // cannot prove it is a real directory -> fail closed
        }
    }

    public void Dispose() => Release();
}

public static class OfflineA6Dependency
{
    public const string PullRequest = "#152";

    /// <summary>Merged into the formal branch: OfflineCoreOperationGate + OfflineLease exist.</summary>
    public const string VerifiedHead = "6dc510bcee76eeca9388c65632636aea6e014549";

    public const string ClosedReason =
        "写入会话未开启：未持有双 Core 独占锁。点击“开启离线写入会话”以获取 OS 级锁。";

    public const string ActivationUnavailable =
        "激活：需要双 Core 独占锁 + 干净激活根；未持有锁时不可激活。发布 ≠ 激活。";
}

/// <summary>
/// Operator-side OFFLINE product management. Every operation goes through the existing A6
/// library on a local solutions.sqlite; there is no Core IPC here and nothing can start,
/// publish or activate production. Publication is an explicit, confirmed immutable write;
/// activation is intentionally absent because it needs the stopped-Core lease from #149.
/// </summary>
public sealed partial class OfflineProductWorkspaceViewModel : ObservableObject
{
    private readonly OfflineWriteGate gate;
    private readonly Dictionary<string, RoiFrameSize> verifiedFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> verifiedFrameSources = new(StringComparer.Ordinal);
    private OfflineProductManager? manager;
    private EditContext? edit;
    private RevisionSnapshot? readSnapshot;
    private RevisionSnapshot? confirmedSnapshot;
    private string databasePath = "";
    private string operatorName = "";
    private bool suppressSelection;
    private bool suppressDirty;
    private bool suppressChannelSync;

    /// <summary>Raised when the operator selects a channel here; the host UI follows the preview.</summary>
    public Action<string>? ChannelSelected { get; set; }

    private sealed record EditContext(
        string SolutionId, string ChannelId, string StationId, long Revision,
        StationRecipe Recipe, PublishedChannelRoi? Roi);

    /// <summary>Operator-confirmed whole-product draft revision snapshot used for publish CAS.</summary>
    private sealed record RevisionSnapshot(
        string SolutionId, IReadOnlyDictionary<string, long> Revisions);

    public OfflineProductWorkspaceViewModel() : this(OfflineWriteGate.Production) { }

    internal OfflineProductWorkspaceViewModel(OfflineWriteGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        this.gate = gate;
        ActivationState = OfflineA6Dependency.ActivationUnavailable;
        SelectedChannel = Channels.First(x => x.ChannelId == SelectedChannelId);
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(RoiX) or nameof(RoiY) or nameof(RoiWidth) or nameof(RoiHeight))
                UpdateRoiImageState();
            if (suppressDirty || edit is null) return;
            if (args.PropertyName is nameof(BrightThreshold) or nameof(MinimumBrightFraction)
                or nameof(SelectedAlgorithm) or nameof(BinaryThresholdText)
                or nameof(MinimumContourAreaText) or nameof(FindBrightDefects)
                or nameof(MaximumDetectionsText) or nameof(RoiX) or nameof(RoiY)
                or nameof(RoiWidth) or nameof(RoiHeight))
                IsDirty = true;
        };
    }

    [ObservableProperty] private string entryPath = "";
    [ObservableProperty] private string operatorId = "";
    [ObservableProperty] private string status = "离线产品管理未启用：请指定 A6 solutions.sqlite 或 run-root 与操作员。";
    [ObservableProperty] private bool isOfflineMode;
    [ObservableProperty] private string writeState = "";
    [ObservableProperty] private string databasePathLabel = "未连接 A6 数据库";
    [ObservableProperty] private OfflineSolutionTile? selectedSolution;
    [ObservableProperty] private string newSolutionId = "PRODUCT-NEW";
    [ObservableProperty] private string newSolutionName = "新产品方案";
    [ObservableProperty] private string selectedChannelId = "CH02";
    [ObservableProperty] private OfflineChannelTile? selectedChannel;
    [ObservableProperty] private string stationId = "-";
    [ObservableProperty] private long stationRevision;
    [ObservableProperty] private double brightThreshold = 127;
    [ObservableProperty] private double minimumBrightFraction = 0.1;
    [ObservableProperty] private OfflineAlgorithmChoice? selectedAlgorithm;
    [ObservableProperty] private bool isContourAlgorithm;
    [ObservableProperty] private bool isBrightnessAlgorithm = true;
    [ObservableProperty] private string binaryThresholdText = "127";
    [ObservableProperty] private string minimumContourAreaText = "8";
    [ObservableProperty] private bool findBrightDefects = true;
    [ObservableProperty] private string maximumDetectionsText = "128";
    [ObservableProperty] private string roiX = "0";
    [ObservableProperty] private string roiY = "0";
    [ObservableProperty] private string roiWidth = "1";
    [ObservableProperty] private string roiHeight = "1";
    [ObservableProperty] private string roiModeText = "尚未读取通道草稿";
    [ObservableProperty] private string roiImageState = "未获得已验证实际图像尺寸：ROI 保存/发布将 fail closed。";
    [ObservableProperty] private string roiPixelPreviewText = "-";
    [ObservableProperty] private bool hasVerifiedImage;
    [ObservableProperty] private string validationMessage = "";
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool publishConfirmed;
    [ObservableProperty] private string publishState = "未发布";
    [ObservableProperty] private string publishedRelease = "未发布";
    [ObservableProperty] private string publishedSha = "";
    [ObservableProperty] private string releaseProvenance = "尚未从 A6 数据库回读任何 Release。";
    [ObservableProperty] private string activationState = OfflineA6Dependency.ActivationUnavailable;
    [ObservableProperty] private int releaseCount;
    [ObservableProperty] private string revisionsSnapshotLabel = "尚未确认全站草稿版本快照";
    // 0 = existing Core ROI editor (default, keeps the native operator-smoke path),
    // 1 = OFFLINE_RECIPE_MANAGEMENT editor. Manual tab selection is two-way bound.
    [ObservableProperty] private int productRoiTabIndex;

    public ObservableCollection<OfflineSolutionTile> Solutions { get; } = new();
    public ObservableCollection<OfflineAlgorithmChoice> AlgorithmChoices { get; } = new();

    public IReadOnlyList<OfflineChannelTile> Channels { get; } =
        ChannelCatalog.All
            .OrderBy(c => c.ChannelId, StringComparer.Ordinal)
            .Select(c => new OfflineChannelTile(c.ChannelId, c.StationId, c.PhysicalDeviceId, c.OwnerRole))
            .ToArray();

    public int PhysicalDeviceCount => Channels.Select(c => c.PhysicalDeviceId).Distinct(StringComparer.Ordinal).Count();
    public int ChannelCount => Channels.Count;
    public string ChannelSummary => ChannelCount + " 逻辑通道 / " + PhysicalDeviceCount + " 物理设备（来自 A0 ChannelCatalog）";
    public string ActiveSolutionId => SelectedSolution?.SolutionId ?? "";
    public bool IsOfflineEditorTab => ProductRoiTabIndex == 1;
    public bool IsLegacyCoreRoiTab => ProductRoiTabIndex == 0;
    public bool A6WritesAllowed => gate.A6WritesAllowed;
    public string WriteGateReason => gate.Reason;
    public string ReleaseSummary => ReleaseCount < 0
        ? "Release 记录不可读（数据库错误）"
        : "数据库已存不可变 Release：" + ReleaseCount + " 个";

    partial void OnProductRoiTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOfflineEditorTab));
        OnPropertyChanged(nameof(IsLegacyCoreRoiTab));
    }

    partial void OnSelectedSolutionChanged(OfflineSolutionTile? value)
    {
        OnPropertyChanged(nameof(ActiveSolutionId));
        if (suppressSelection) return;
        DiscardEdit("已切换产品 " + (value?.SolutionId ?? "-") +
            "：未保存修改不会写入任何产品草稿，必须重新读取通道草稿。");
    }

    partial void OnPublishConfirmedChanged(bool value)
    {
        if (!value) { confirmedSnapshot = null; return; }
        // Confirmation binds the FROZEN snapshot captured when the operator last read the
        // channel draft. It never re-reads newer revisions and never silently adopts them.
        var snapshot = readSnapshot;
        if (snapshot is null || SelectedSolution is null ||
            !string.Equals(snapshot.SolutionId, SelectedSolution.SolutionId, StringComparison.Ordinal))
        {
            confirmedSnapshot = null;
            PublishConfirmed = false;
            RevisionsSnapshotLabel =
                "确认被拒绝：请先读取通道草稿以冻结全站版本，再勾选确认。";
            Status = RevisionsSnapshotLabel;
            return;
        }
        confirmedSnapshot = snapshot;
        RevisionsSnapshotLabel = "已确认绑定的冻结版本（发布 CAS）：" + DescribeSnapshot(snapshot);
    }

    partial void OnSelectedChannelChanged(OfflineChannelTile? value)
    {
        if (!suppressChannelSync && !string.Equals(SelectedChannelId, value?.ChannelId, StringComparison.Ordinal))
        {
            suppressChannelSync = true;
            try { SelectedChannelId = value?.ChannelId ?? ""; }
            finally { suppressChannelSync = false; }
        }
        if (suppressSelection) return;
        DiscardEdit("已切换通道 " + (value?.ChannelId ?? "-") +
            "：旧草稿不会保存到新通道，必须重新读取。");
        UpdateRoiImageState();
        if (value is not null) ChannelSelected?.Invoke(value.ChannelId);
    }

    partial void OnSelectedChannelIdChanged(string value)
    {
        if (suppressChannelSync) return;
        var tile = Channels.FirstOrDefault(x => string.Equals(x.ChannelId, value, StringComparison.Ordinal));
        if (tile is null) return;
        suppressChannelSync = true;
        try { SelectedChannel = tile; }
        finally { suppressChannelSync = false; }
    }

    partial void OnSelectedAlgorithmChanged(OfflineAlgorithmChoice? value)
    {
        IsContourAlgorithm = string.Equals(value?.AlgorithmId,
            OfflineRecipeValidator.OpenCvContourAlgorithm, StringComparison.Ordinal);
        IsBrightnessAlgorithm = !IsContourAlgorithm;
    }

    partial void OnReleaseCountChanged(int value) => OnPropertyChanged(nameof(ReleaseSummary));

    /// <summary>Enables offline management. Nothing is written until an explicit command.</summary>
    public void Attach(string solutionsDbPathOrRunRoot, string actor)
    {
        ArgumentNullException.ThrowIfNull(solutionsDbPathOrRunRoot);
        // Switching databases must never retain the previous database's products,
        // revision snapshot, confirmation or publish read-back evidence.
        ResetAttachment("正在附加 A6 数据库：" + solutionsDbPathOrRunRoot);
        try
        {
            SolutionSafety.RequireActor(actor);
            string db = OfflineProductEntry.ResolveSolutionsDatabase(solutionsDbPathOrRunRoot);
            if (!File.Exists(db))
                throw new FileNotFoundException("A6 solutions.sqlite 不存在；不会创建新数据库。", db);
            // The writer lease is bound to ONE run root. Opening a different database while a
            // lease is held would be a fail-open write authorisation, so fail closed and drop
            // the mismatched lease instead of writing into an unlocked store.
            bool leaseCoversDb = gate.CoversDatabase(db);
            if (gate.A6WritesAllowed && !leaseCoversDb)
            {
                gate.Release();
                WriteState = "写入会话已失效：它绑定的是 " + gate.DataRoot +
                             "，当前数据库属于 " + (Path.GetDirectoryName(db) ?? "?") + "；本次以只读附加。";
            }
            bool writable = gate.A6WritesAllowed && gate.CoversDatabase(db);
            manager = OfflineProductManager.Open(db, actor, readOnly: !writable);
            operatorName = actor;
            databasePath = db;
            OperatorId = actor;
            IsOfflineMode = true;
            DatabasePathLabel = db;
            WriteState = gate.A6WritesAllowed
                ? "A6 写入已启用（TEST_ONLY 隔离数据库；无 Core、无激活）。"
                : "A6 写入已 fail closed：" + gate.Reason;
            if (!RefreshCore(out string readError))
            {
                ResetAttachment("启用离线方案管理失败（数据库不可读；未修复、未建表）：" +
                    readError);
                return;
            }
            if (!gate.A6WritesAllowed)
                Status = "离线方案管理已就绪（只读附加）：" + gate.Reason;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
            or InvalidOperationException or KeyNotFoundException or DbException)
        {
            // Never strand the OS handles on a failed switch: fail closed and release.
            gate.Release();
            ResetAttachment("启用离线方案管理失败（已释放双锁）：" + ex.Message);
        }
    }

    /// <summary>
    /// Opens the real offline writer session: acquires and HOLDS both Core OS locks for the run
    /// root, verifies the activation root is clean, then re-attaches so A6 opens writable.
    /// Any failure releases whatever was taken and leaves writes closed; read-only preview stays.
    /// </summary>
    [RelayCommand]
    public void EnableOfflineWrites()
    {
        string entry = (EntryPath ?? "").Trim();
        if (entry.Length == 0)
        {
            WriteState = "开启写入会话失败：请先填写运行根目录。";
            return;
        }
        string actor = string.IsNullOrWhiteSpace(OperatorId) ? operatorName : OperatorId;
        string db;
        try
        {
            // Resolve the EXISTING database first, then derive the run root from it. The operator
            // may legitimately give either the run root or .../solutions.sqlite.
            db = OfflineProductEntry.ResolveSolutionsDatabase(entry);
            if (!File.Exists(db))
                throw new FileNotFoundException("A6 solutions.sqlite 不存在；不会创建新数据库。", db);
            SolutionSafety.RequireActor(actor);
            string runRoot = Path.GetDirectoryName(Path.GetFullPath(db))!;
            gate.Arm(runRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            gate.Release();
            WriteState = "开启写入会话失败（写入保持关闭，未创建/修复数据库）：" + ex.Message;
            Status = WriteState;
            return;
        }
        Attach(entry, actor);
        // Transactional result: only claim success when the writer is genuinely bound to THIS db.
        if (!IsOfflineMode || manager is null || !gate.CoversDatabase(databasePath))
        {
            gate.Release();
            WriteState = "开启写入会话失败（已在失败后释放双锁）：" + Status;
            return;
        }
        WriteState = "写入会话已开启：" + gate.Reason;
    }

    /// <summary>Closes the writer session and releases both Core locks.</summary>
    [RelayCommand]
    public void DisableOfflineWrites()
    {
        gate.Release();
        // A written-through manager and its revision/release evidence must not survive the
        // session: invalidate them and fall back to a clean (detached) state.
        ResetAttachment("写入会话已关闭，已丢弃可写连接与修订证据：" + gate.Reason);
        manager = null;
        edit = null;
        WriteState = "写入会话已关闭：" + gate.Reason;
        Status = WriteState;
    }

    /// <summary>Called by the window on close so the OS locks can never leak.</summary>
    public void ReleaseWriterSession() => gate.Release();

    [RelayCommand]
    public void AttachEntry()
    {
        // Explicit offline entry switches the inner product tab to the new editor.
        ProductRoiTabIndex = 1;
        Attach(EntryPath, OperatorId);
    }

    public void ReportLaunchFailure(string message) =>
        Status = "OFFLINE_RECIPE_MANAGEMENT 启动参数无效：" + message + "（未连接任何 A6 数据库）";

    [RelayCommand]
    public void Refresh()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        if (!RefreshCore(out string error)) Status = "读取方案失败：" + error;
    }

    private bool RefreshCore(out string error)
    {
        error = "";
        var current = manager;
        if (current is null) { error = "未启用离线方案管理。"; return false; }
        try
        {
            string? previous = SelectedSolution?.SolutionId;
            var items = current.ListSolutions()
                .Select(x => new OfflineSolutionTile(x.SolutionId, x.DisplayName)).ToArray();
            suppressSelection = true;
            try
            {
                Solutions.Clear();
                foreach (var item in items) Solutions.Add(item);
                SelectedSolution = items.FirstOrDefault(x =>
                    string.Equals(x.SolutionId, previous, StringComparison.Ordinal)) ?? items.FirstOrDefault();
            }
            finally { suppressSelection = false; }
            OnPropertyChanged(nameof(ActiveSolutionId));
            if (SelectedSolution is null)
                Status = "A6 数据库中还没有产品方案。";
            else if (previous is not null && !string.Equals(previous, SelectedSolution.SolutionId, StringComparison.Ordinal))
                DiscardEdit("当前产品已不在数据库中：未保存修改不会写入任何产品草稿。");
            else
                Status = "已读取 " + items.Length + " 个真实 A6 方案（不是 UI 假数据）。";
            RefreshReleaseCount();
            InvalidateRevisionSnapshot("数据库已重新读取，需重新读取通道草稿");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException
            or InvalidDataException or KeyNotFoundException or DbException)
        {
            error = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    public void CreateProduct()
    {
        if (!RequireWrites("创建产品草稿")) return;
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        try
        {
            string id = NewSolutionId.Trim();
            string name = NewSolutionName.Trim();
            OfflineProductManager.ValidateNewSolution(id, name);
            manager.CreateSimSolution(id, name);
            Refresh();
            suppressSelection = true;
            try
            {
                SelectedSolution = Solutions.FirstOrDefault(x =>
                    string.Equals(x.SolutionId, id, StringComparison.Ordinal));
            }
            finally { suppressSelection = false; }
            OnPropertyChanged(nameof(ActiveSolutionId));
            InvalidateRevisionSnapshot("新产品尚未读取通道草稿");
            Status = "已创建产品 " + id + "（" + TopologySummary() + " 草稿，仍未发布）。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException or DbException)
        {
            RefreshCore(out _);
            InvalidateRevisionSnapshot("创建产品失败，需重新读取");
            Status = "创建产品失败（A6 单事务已回滚，未写入任何产品/工位草稿）：" +
                ex.Message + "；已刷新数据库核查，请勿把任何残留当作完整产品。";
        }
    }

    [RelayCommand]
    public void LoadChannel()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        var solution = SelectedSolution;
        var channel = SelectedChannel;
        if (solution is null) { Status = "请先选择产品方案。"; return; }
        if (channel is null) { Status = "请先选择通道。"; return; }
        try
        {
            var view = manager.ReadChannel(solution.SolutionId, channel.ChannelId);
            var stationDraft = manager.Repository.GetDraft(solution.SolutionId, view.StationId)
                ?? throw new KeyNotFoundException("Missing product station draft.");
            if (view.StationRevision != stationDraft.Revision)
                throw new InvalidOperationException(
                    "读取期间草稿被并发修改：请重新读取通道草稿，禁止混用两次读取。");
            var baselineRoi = RoiReleaseWorkflow.ReadStationRoi(stationDraft.Recipe, channel.ChannelId);
            edit = new EditContext(solution.SolutionId, channel.ChannelId, view.StationId,
                stationDraft.Revision, stationDraft.Recipe, baselineRoi);
            suppressDirty = true;
            try
            {
                StationId = view.StationId;
                StationRevision = stationDraft.Revision;
                LoadAlgorithm(view, stationDraft.Recipe);
                double threshold = Number(view.StationTunables, "brightThreshold", 127);
                double fraction = Number(view.StationTunables, "minimumBrightFraction", 0.1);
                if (view.InspectionTarget?.AlgorithmId == OfflineRecipeValidator.LegacyBrightnessAlgorithm)
                {
                    threshold = Number(view.InspectionTarget.NumericParameters, "brightThreshold", threshold);
                    fraction = Number(view.InspectionTarget.NumericParameters, "minimumBrightFraction", fraction);
                }
                BrightThreshold = threshold;
                MinimumBrightFraction = fraction;
                RoiX = Format(view.InspectionRoi.X);
                RoiY = Format(view.InspectionRoi.Y);
                RoiWidth = Format(view.InspectionRoi.Width);
                RoiHeight = Format(view.InspectionRoi.Height);
                RoiModeText = baselineRoi is null
                    ? "草稿中无 ROI 覆盖（等价全帧）"
                    : "草稿 ROI 源 revision=" + baselineRoi.SourceRevision + "（改变后会递增）";
                ValidationMessage = "";
                IsDirty = false;
            }
            finally { suppressDirty = false; }
            UpdateRoiImageState();
            bool frozen = CaptureReadSnapshot();
            Status = frozen
                ? "已读取草稿 " + view.ChannelId + " @ " + view.StationId +
                    " revision=" + stationDraft.Revision + "（草稿，未发布；插件=" + PluginLabel() + "）"
                : "已读取草稿 " + view.ChannelId + "，但全站版本快照冻结失败（并发修改或读取失败）：发布保持 fail closed，请重新读取。";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException
            or ArgumentException or InvalidOperationException or IOException or DbException)
        {
            edit = null;
            ValidationMessage = "";
            Status = "读取草稿失败：" + ex.Message;
        }
    }

    [RelayCommand]
    public void Validate()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        if (!TryBuildRecipe(out _, out var issues)) return;
        if (issues.Count != 0)
        {
            ValidationMessage = OfflineRecipeValidator.Describe(issues);
            Status = "校验失败，未写入：" + ValidationMessage;
            return;
        }
        ValidationMessage = "校验通过：参数、已安装插件与 ROI 合法（尚未保存）。";
        Status = ValidationMessage;
    }

    [RelayCommand]
    public void SaveDraft()
    {
        if (!RequireWrites("保存草稿")) return;
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        if (edit is null) { Status = "请先读取一个通道草稿。"; return; }
        if (!IsDirty) { Status = "没有未保存修改，未写入任何草稿。"; return; }
        if (!TryBuildRecipe(out var recipe, out var issues)) return;
        if (issues.Count != 0)
        {
            ValidationMessage = OfflineRecipeValidator.Describe(issues);
            Status = "保存被拒绝（未写入）：" + ValidationMessage;
            return;
        }
        var target = edit;
        try
        {
            var saved = manager.SaveStation(target.SolutionId, recipe, target.Revision, verifiedFrames);
            var savedRoi = RoiReleaseWorkflow.ReadStationRoi(saved.Recipe, target.ChannelId);
            edit = new EditContext(target.SolutionId, target.ChannelId, target.StationId,
                saved.Revision, saved.Recipe, savedRoi);
            StationRevision = saved.Revision;
            IsDirty = false;
            bool frozen = CaptureReadSnapshot();
            PublishConfirmed = false;
            ValidationMessage = frozen
                ? "草稿已保存 revision=" + saved.Revision + "。"
                : "草稿已保存，但全站版本快照冻结失败；发布需重新读取。";
            Status = frozen
                ? "草稿已保存 revision=" + saved.Revision + "（仍未发布，不影响 Active/Epoch）。"
                : "草稿已保存 revision=" + saved.Revision + "，但版本快照冻结失败（发布已 fail closed）。";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("DRAFT_REVISION_CONFLICT", StringComparison.Ordinal))
        {
            Status = "保存草稿被拒绝：草稿版本冲突（CAS），未写入；请重新读取通道草稿。";
            ValidationMessage = Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException or DbException)
        {
            Status = "保存草稿被拒绝（未写入）：" + ex.Message;
            ValidationMessage = Status;
        }
    }

    [RelayCommand]
    public void Publish()
    {
        if (!RequireWrites("发布")) return;
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        if (SelectedSolution is null) { Status = "请先选择产品方案。"; return; }
        if (!PublishConfirmed)
        {
            PublishState = "未发布：需要先勾选「我确认发布不可变 Release」。";
            Status = PublishState;
            return;
        }
        if (IsDirty)
        {
            PublishState = "未发布：存在未保存的草稿修改。";
            Status = PublishState + " 请先保存或重新读取草稿。";
            return;
        }
        var solution = SelectedSolution;
        var snapshot = confirmedSnapshot;
        if (snapshot is null ||
            !string.Equals(snapshot.SolutionId, solution.SolutionId, StringComparison.Ordinal))
        {
            PublishState = "未发布：没有已确认的冻结全站版本快照；请重新读取通道草稿后再次确认。";
            Status = PublishState;
            return;
        }
        try
        {
            // The repository re-verifies this whole-product revision snapshot (CAS) and runs
            // plugin/ROI validation inside ONE immediate A6 transaction before any insert.
            var result = manager.Publish(solution.SolutionId, snapshot.Revisions, verifiedFrames);
            if (result.Outcome != OfflinePublishOutcome.Published || result.Release is null)
            {
                PublishState = "未发布（发布被拒绝，数据库未新增 Release）。";
                PublishedRelease = "未发布";
                PublishedSha = "";
                ReleaseProvenance = result.Message;
                ValidationMessage = result.Message;
                Status = "发布被拒绝：" + result.Message +
                    " 已确认版本失效，请重新读取通道草稿。";
                InvalidateRevisionSnapshot("发布被拒绝，需重新读取通道草稿");
                return;
            }
            // Prove the immutable row really persisted: reopen a NEW read-only manager.
            var reopened = OfflineProductManager.Open(databasePath, operatorName, readOnly: true)
                .RequirePublished(result.Release.ReleaseId);
            reopened.Verify();
            PublishedRelease = reopened.ReleaseId;
            PublishedSha = reopened.ContentSha256;
            ReleaseProvenance = "A6 SQLite 重新打开回读 · " + databasePath +
                " · published " + reopened.PublishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                " · stations=" + reopened.Manifest.Stations.Count;
            PublishState = "已发布不可变 Release（数据库回读通过）；未激活。";
            ValidationMessage = "";
            PublishConfirmed = false;
            RefreshReleaseCount();
            CaptureReadSnapshot();
            Status = "已发布不可变 Release：" + reopened.ReleaseId +
                " · 完整 SHA256 " + reopened.ContentSha256 +
                "（激活仍不可用：#149 未合并）。";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
            or ArgumentException or KeyNotFoundException or IOException or DbException)
        {
            PublishState = "发布失败，未确认任何成功（数据库状态需重新读取）。";
            PublishedRelease = "未回读";
            PublishedSha = "";
            ReleaseProvenance = ex.Message;
            Status = "发布失败（禁止显示成功）：" + ex.Message;
        }
    }

    [RelayCommand]
    public void ReopenPersistedRelease()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        try
        {
            var releases = manager.ListReleases();
            var scoped = SelectedSolution is null
                ? releases
                : releases.Where(x => string.Equals(x.Manifest.SolutionId,
                    SelectedSolution.SolutionId, StringComparison.Ordinal)).ToArray();
            var latest = scoped.LastOrDefault();
            if (latest is null)
            {
                PublishedRelease = "未发布";
                PublishedSha = "";
                PublishState = "未发布";
                ReleaseProvenance = "A6 数据库中没有匹配的已发布 Release。";
                Status = ReleaseProvenance;
                return;
            }
            var reopened = OfflineProductManager.Open(databasePath, operatorName, readOnly: true)
                .RequirePublished(latest.ReleaseId);
            reopened.Verify();
            PublishedRelease = reopened.ReleaseId;
            PublishedSha = reopened.ContentSha256;
            PublishState = "已发布不可变 Release（数据库回读通过）；未激活。";
            ReleaseProvenance = "A6 SQLite 重新打开回读 · " + databasePath +
                " · published " + reopened.PublishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                " · stations=" + reopened.Manifest.Stations.Count;
            RefreshReleaseCount();
            Status = "已从 A6 数据库重新读取 Release " + reopened.ReleaseId +
                "（完整 SHA256 见发布面板；激活仍不可用）。";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
            or ArgumentException or KeyNotFoundException or IOException or DbException)
        {
            PublishedRelease = "未回读";
            PublishedSha = "";
            Status = "回读 Release 失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Mirrors a host-side channel selection (device map / gallery) without echoing the
    /// selection event back. Any prior draft context is invalidated, so an old draft can
    /// never be saved into the newly selected product/channel.
    /// </summary>
    public void SyncSelectedChannel(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return;
        var tile = Channels.FirstOrDefault(x =>
            string.Equals(x.ChannelId, channelId, StringComparison.Ordinal));
        if (tile is null || ReferenceEquals(tile, SelectedChannel)) return;
        suppressSelection = true;
        try { SelectedChannel = tile; }
        finally { suppressSelection = false; }
        DiscardEdit("主界面切换到通道 " + channelId + "：未保存修改已丢弃，不会写入任何产品草稿。");
        UpdateRoiImageState();
    }

    /// <summary>
    /// Records the actual dimensions of a verified Core PNG for one channel. ROI pixel
    /// association uses only these dimensions, never an assumed resolution.
    /// </summary>
    public void ReportVerifiedImage(string channelId, int width, int height, string provenance)
    {
        if (string.IsNullOrWhiteSpace(channelId) || width <= 0 || height <= 0 ||
            string.IsNullOrWhiteSpace(provenance)) return;
        if (!ChannelCatalog.All.Any(x => string.Equals(x.ChannelId, channelId, StringComparison.Ordinal)))
            return;
        verifiedFrames[channelId] = new RoiFrameSize(width, height);
        verifiedFrameSources[channelId] = provenance;
        if (SelectedChannel is not null &&
            string.Equals(SelectedChannel.ChannelId, channelId, StringComparison.Ordinal))
            UpdateRoiImageState();
    }

    private bool RequireWrites(string action)
    {
        if (gate.A6WritesAllowed && gate.CoversDatabase(databasePath)) return true;
        Status = action + " 已 fail closed：" + gate.Reason;
        ValidationMessage = Status;
        return false;
    }

    private void DiscardEdit(string reason)
    {
        bool had = edit is not null || IsDirty;
        edit = null;
        StationId = "-";
        StationRevision = 0;
        ValidationMessage = "";
        suppressDirty = true;
        try
        {
            IsDirty = false;
            PublishConfirmed = false;
            RoiModeText = "尚未读取通道草稿";
        }
        finally { suppressDirty = false; }
        UpdateRoiImageState();
        InvalidateRevisionSnapshot("编辑目标已切换，需重新读取通道草稿");
        InvalidatePublishedEvidence("编辑目标已切换：旧发布回读状态失效。");
        if (had) Status = reason;
    }

    private void LoadAlgorithm(DraftChannelParameterView view, StationRecipe recipe)
    {
        var choices = new List<OfflineAlgorithmChoice>
        {
            OfflineAlgorithmChoice.StationDefault,
            new(OfflineRecipeValidator.LegacyBrightnessAlgorithm,
                "legacy.sim.brightness.v1（站亮度规则）", true),
            new(OfflineRecipeValidator.OpenCvContourAlgorithm,
                "opencv.contour（轮廓检测）", true)
        };
        var target = view.InspectionTarget;
        if (target is not null && choices.All(x => !string.Equals(x.AlgorithmId,
                target.AlgorithmId, StringComparison.Ordinal)))
            choices.Add(new(target.AlgorithmId, "未安装：" + target.AlgorithmId + "（禁止保存/发布）", false));
        AlgorithmChoices.Clear();
        foreach (var choice in choices) AlgorithmChoices.Add(choice);
        SelectedAlgorithm = choices.First(x => string.Equals(x.AlgorithmId,
            target?.AlgorithmId ?? "", StringComparison.Ordinal));

        if (string.Equals(target?.AlgorithmId, OfflineRecipeValidator.OpenCvContourAlgorithm,
                StringComparison.Ordinal) && target is not null)
        {
            BinaryThresholdText = Format(Number(target.NumericParameters, "binaryThreshold", 127));
            MinimumContourAreaText = Format(Number(target.NumericParameters,
                "minimumContourAreaPixels", 8));
            FindBrightDefects = Number(target.NumericParameters, "findBrightDefects", 1) == 1;
            MaximumDetectionsText = Format(Number(target.NumericParameters, "maximumDetections", 128));
        }
        else
        {
            BinaryThresholdText = "127";
            MinimumContourAreaText = "8";
            FindBrightDefects = true;
            MaximumDetectionsText = "128";
        }
    }

    private string PluginLabel() => SelectedAlgorithm is { AlgorithmId.Length: > 0 } choice
        ? choice.AlgorithmId
        : OfflineRecipeValidator.LegacyBrightnessAlgorithm + "（站默认）";

    private bool TryBuildRecipe(out StationRecipe recipe, out IReadOnlyList<OfflineRecipeIssue> issues)
    {
        recipe = null!;
        issues = Array.Empty<OfflineRecipeIssue>();
        var target = edit;
        if (target is null) { Status = "请先读取一个通道草稿。"; return false; }
        if (SelectedSolution is null || !string.Equals(SelectedSolution.SolutionId, target.SolutionId, StringComparison.Ordinal) ||
            SelectedChannel is null || !string.Equals(SelectedChannel.ChannelId, target.ChannelId, StringComparison.Ordinal))
        {
            Status = "编辑目标已切换：拒绝用旧草稿写入当前产品/通道。";
            return false;
        }
        if (!double.IsFinite(BrightThreshold) || !double.IsFinite(MinimumBrightFraction))
        {
            Status = "校验失败，未写入：亮度阈值/比例必须是有限数值。";
            return false;
        }
        if (!TryParseRoi(out double x, out double y, out double width, out double height))
        {
            Status = "校验失败，未写入：ROI 坐标必须是有效小数（英文小数点）。";
            return false;
        }
        NormalizedInspectionRoi bounds;
        try { bounds = new NormalizedInspectionRoi(x, y, width, height); bounds.Validate(target.ChannelId); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            Status = "校验失败，未写入：ROI 无效 — " + ex.Message;
            return false;
        }

        var tunables = new Dictionary<string, double>(target.Recipe.Tunables, StringComparer.Ordinal)
        {
            ["brightThreshold"] = BrightThreshold,
            ["minimumBrightFraction"] = MinimumBrightFraction
        };
        string prefix = "roi." + target.ChannelId + ".";
        bool roiChanged = target.Roi is null
            ? bounds != NormalizedInspectionRoi.FullFrame
            : bounds != target.Roi.Bounds;
        if (roiChanged)
        {
            if (!verifiedFrames.TryGetValue(target.ChannelId, out var frame))
            {
                Status = "保存/发布被拒绝（fail closed）：ROI " + target.ChannelId +
                    " 未关联已验证实际图像尺寸。请先在本页显示该通道的 Core 已验证 PNG。";
                return false;
            }
            try { _ = bounds.ToPixels(frame.Width, frame.Height); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            {
                Status = "校验失败，未写入：ROI 超出实际图像 — " + ex.Message;
                return false;
            }
            foreach (string key in tunables.Keys.Where(k =>
                k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                tunables.Remove(key);
            long revision = (target.Roi?.SourceRevision ?? 0) + 1;
            tunables[prefix + "x"] = bounds.X;
            tunables[prefix + "y"] = bounds.Y;
            tunables[prefix + "width"] = bounds.Width;
            tunables[prefix + "height"] = bounds.Height;
            tunables[prefix + "revision"] = revision;
        }

        var inspectors = (target.Recipe.InspectionTargets ?? [])
            .Where(t => !string.Equals(t.ChannelId, target.ChannelId, StringComparison.Ordinal))
            .ToList();
        var algorithm = SelectedAlgorithm ?? OfflineAlgorithmChoice.StationDefault;
        if (algorithm.AlgorithmId.Length != 0)
        {
            if (!algorithm.Installed)
            {
                Status = "校验失败，未写入：未安装的检测插件 " + algorithm.AlgorithmId + "（fail closed）。";
                return false;
            }
            Dictionary<string, double> numbers;
            if (string.Equals(algorithm.AlgorithmId, OfflineRecipeValidator.LegacyBrightnessAlgorithm,
                    StringComparison.Ordinal))
            {
                numbers = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["brightThreshold"] = BrightThreshold,
                    ["minimumBrightFraction"] = MinimumBrightFraction
                };
            }
            else if (string.Equals(algorithm.AlgorithmId, OfflineRecipeValidator.OpenCvContourAlgorithm,
                         StringComparison.Ordinal))
            {
                if (!TryParseContourNumbers(out numbers))
                {
                    Status = "校验失败，未写入：轮廓插件参数必须是数值（英文小数点）。";
                    return false;
                }
            }
            else
            {
                Status = "校验失败，未写入：未安装的检测插件 " + algorithm.AlgorithmId + "（fail closed）。";
                return false;
            }
            inspectors.Add(new InspectionAlgorithmTarget(target.ChannelId, algorithm.AlgorithmId,
                new SortedDictionary<string, double>(numbers, StringComparer.Ordinal),
                new SortedDictionary<string, string>(StringComparer.Ordinal)));
        }
        recipe = target.Recipe with
        {
            Tunables = tunables,
            InspectionTargets = inspectors.Count == 0 ? null : inspectors.ToArray()
        };
        issues = OfflineRecipeValidator.Inspect(recipe, verifiedFrames, target.Recipe);
        return true;
    }

    private bool TryParseContourNumbers(out Dictionary<string, double> numbers)
    {
        numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        const NumberStyles style = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;
        if (!double.TryParse(BinaryThresholdText, style, culture, out double binary) ||
            !double.TryParse(MinimumContourAreaText, style, culture, out double area) ||
            !double.TryParse(MaximumDetectionsText, style, culture, out double detections))
            return false;
        numbers["binaryThreshold"] = binary;
        numbers["minimumContourAreaPixels"] = area;
        numbers["findBrightDefects"] = FindBrightDefects ? 1 : 0;
        numbers["maximumDetections"] = detections;
        return true;
    }

    private bool TryParseRoi(out double x, out double y, out double width, out double height)
    {
        const NumberStyles style = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;
        bool parsed = double.TryParse(RoiX, style, culture, out x)
            & double.TryParse(RoiY, style, culture, out y)
            & double.TryParse(RoiWidth, style, culture, out width)
            & double.TryParse(RoiHeight, style, culture, out height);
        return parsed && double.IsFinite(x) && double.IsFinite(y)
            && double.IsFinite(width) && double.IsFinite(height);
    }

    private void UpdateRoiImageState()
    {
        string? channelId = SelectedChannel?.ChannelId;
        if (channelId is null)
        {
            HasVerifiedImage = false;
            RoiImageState = "未选择通道。";
            RoiPixelPreviewText = "-";
            return;
        }
        if (!verifiedFrames.TryGetValue(channelId, out var frame))
        {
            HasVerifiedImage = false;
            RoiImageState = "未获得通道 " + channelId +
                " 已验证实际图像尺寸：改变 ROI 的保存/发布将 fail closed。";
            RoiPixelPreviewText = "-";
            return;
        }
        HasVerifiedImage = true;
        string source = verifiedFrameSources.TryGetValue(channelId, out var provenance)
            ? provenance : "已验证 PNG";
        RoiImageState = "已验证实际图像 " + frame.Width + "×" + frame.Height +
            " · 通道 " + channelId + " · " + source;
        if (!TryParseRoi(out double x, out double y, out double width, out double height))
        {
            RoiPixelPreviewText = "ROI 坐标格式无效。";
            return;
        }
        try
        {
            var pixels = new NormalizedInspectionRoi(x, y, width, height)
                .ToPixels(frame.Width, frame.Height);
            RoiPixelPreviewText = "像素区域 X=" + pixels.X + " Y=" + pixels.Y +
                " W=" + pixels.Width + " H=" + pixels.Height;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            RoiPixelPreviewText = "ROI 无法映射到实际图像：" + ex.Message;
        }
    }

    /// <summary>
    /// Freezes the whole-product draft revisions the operator is actually shown when a
    /// channel draft is read/saved. Publication re-verifies these exact values (CAS) inside
    /// one A6 transaction; nothing re-reads or silently adopts newer revisions at confirm time.
    /// </summary>
    private bool CaptureReadSnapshot()
    {
        readSnapshot = null;
        confirmedSnapshot = null;
        if (manager is null || SelectedSolution is null)
        {
            RevisionsSnapshotLabel = "尚未读取全站草稿版本快照";
            return false;
        }
        try
        {
            var revisions = manager.ListDraftRevisions(SelectedSolution.SolutionId);
            if (edit is not null &&
                (!revisions.TryGetValue(edit.StationId, out long shown) || shown != edit.Revision))
            {
                InvalidateRevisionSnapshot(
                    "读取期间该工位草稿被并发修改，需重新读取通道草稿");
                return false;
            }
            readSnapshot = new RevisionSnapshot(SelectedSolution.SolutionId, revisions);
            RevisionsSnapshotLabel = "已读取并冻结全站草稿版本（发布 CAS）：" +
                DescribeSnapshot(readSnapshot);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
            or ArgumentException or IOException or DbException)
        {
            InvalidateRevisionSnapshot("全站版本读取失败，发布保持 fail closed：" + ex.Message);
            return false;
        }
    }

    /// <summary>Drops any frozen/confirmed revision snapshot; publication then requires a re-read.</summary>
    private void InvalidateRevisionSnapshot(string reason)
    {
        readSnapshot = null;
        confirmedSnapshot = null;
        PublishConfirmed = false;
        RevisionsSnapshotLabel = "尚未确认全站草稿版本快照（" + reason + "）";
    }

    /// <summary>The displayed Release read-back must never survive a database/product switch.</summary>
    private void InvalidatePublishedEvidence(string reason)
    {
        PublishedRelease = "未发布";
        PublishedSha = "";
        PublishState = "未发布";
        ReleaseProvenance = reason;
    }

    /// <summary>Clears the whole attachment so a failed/replaced database leaves no old evidence.</summary>
    private void ResetAttachment(string status)
    {
        // Switching databases must not leak ROI dimension/provenance evidence across run roots.
        verifiedFrames.Clear();
        verifiedFrameSources.Clear();
        readSnapshot = null;
        confirmedSnapshot = null;
        manager = null;
        operatorName = "";
        databasePath = "";
        DatabasePathLabel = "未连接 A6 数据库";
        IsOfflineMode = false;
        WriteState = "";
        edit = null;
        suppressSelection = true;
        try
        {
            Solutions.Clear();
            SelectedSolution = null;
        }
        finally { suppressSelection = false; }
        OnPropertyChanged(nameof(ActiveSolutionId));
        suppressDirty = true;
        try
        {
            StationId = "-";
            StationRevision = 0;
            IsDirty = false;
            RoiModeText = "尚未读取通道草稿";
            ValidationMessage = "";
        }
        finally { suppressDirty = false; }
        ReleaseCount = 0;
        InvalidateRevisionSnapshot("尚未附加数据库");
        InvalidatePublishedEvidence("未附加数据库：没有可确认的发布回读证据。");
        Status = status;
    }

    private static string DescribeSnapshot(RevisionSnapshot snapshot) =>
        string.Join(" · ", snapshot.Revisions.Select(x => x.Key + "=r" + x.Value)) +
        " · " + snapshot.Revisions.Count + " 工位";

    private static string TopologySummary()
    {
        int stations = ChannelCatalog.All.Select(c => c.StationId)
            .Distinct(StringComparer.Ordinal).Count();
        int devices = ChannelCatalog.All.Select(c => c.PhysicalDeviceId)
            .Distinct(StringComparer.Ordinal).Count();
        return stations + " 工位 / " + devices + " 物理设备 / " + ChannelCatalog.All.Count + " 逻辑通道";
    }

    private void RefreshReleaseCount()
    {
        try { ReleaseCount = manager?.ListReleases().Count ?? 0; }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
            or ArgumentException or IOException)
        {
            ReleaseCount = -1;
        }
    }

    private static double Number(IReadOnlyDictionary<string, double> source, string key, double fallback) =>
        source.TryGetValue(key, out double value) && double.IsFinite(value) ? value : fallback;

    private static string Format(double value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);
}