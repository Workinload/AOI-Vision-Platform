using AOI.Domain;
using AOI.Solutions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AOI.Ui.Avalonia;

public sealed record OfflineChannelTile(
    string ChannelId, string StationId, string PhysicalDeviceId, string Owner);

/// <summary>
/// Operator-side OFFLINE product management. Every operation goes through the existing A6
/// library on a local solutions.sqlite; there is no Core IPC here and nothing can start or
/// write production. Activation is intentionally not implemented: it needs the stopped-Core
/// lease from A0 PR #149 and must never be faked from the UI.
/// </summary>
public sealed partial class OfflineProductWorkspaceViewModel : ObservableObject
{
    private OfflineProductManager? manager;
    private StationDraft? draft;

    [ObservableProperty] private string status = "离线方案管理未启用：请指定 A6 方案数据库与操作员。";
    [ObservableProperty] private bool isOfflineMode;
    [ObservableProperty] private string activeSolutionId = "";
    [ObservableProperty] private string selectedChannelId = "CH02";
    [ObservableProperty] private string stationId = "-";
    [ObservableProperty] private long stationRevision;
    [ObservableProperty] private double brightThreshold = 127;
    [ObservableProperty] private double minimumBrightFraction = 0.1;
    [ObservableProperty] private string publishedRelease = "未发布";
    [ObservableProperty] private string publishedSha = "";
    [ObservableProperty] private string activationState = "未激活：需要双 Core 停止门禁（#149）";

    public ObservableCollection<string> Solutions { get; } = new();

    public IReadOnlyList<OfflineChannelTile> Channels { get; } =
        ChannelCatalog.All
            .OrderBy(c => c.ChannelId, StringComparer.Ordinal)
            .Select(c => new OfflineChannelTile(c.ChannelId, c.StationId, c.PhysicalDeviceId,
                c.ChannelId == "CH01" ? "MASTER" : "MASTER/SLAVE"))
            .ToArray();

    public int PhysicalDeviceCount => Channels.Select(c => c.PhysicalDeviceId).Distinct().Count();
    public int ChannelCount => Channels.Count;

    /// <summary>Enables offline management. Nothing is written until an explicit command.</summary>
    public void Attach(string solutionsDbPath, string actor)
    {
        manager = OfflineProductManager.Open(solutionsDbPath, actor);
        IsOfflineMode = true;
        Status = "离线方案管理已就绪（本机 A6 数据库，双 Core 必须停止）。";
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        try
        {
            Solutions.Clear();
            foreach (var solution in manager.ListSolutions()) Solutions.Add(solution.SolutionId);
            if (Solutions.Count == 0)
                Status = "A6 数据库中还没有产品方案。";
            else
                Status = "已读取 " + Solutions.Count + " 个真实 A6 方案（非 UI 假数据）。";
            if (string.IsNullOrEmpty(ActiveSolutionId) && Solutions.Count > 0)
                ActiveSolutionId = Solutions[0];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Status = "读取方案失败：" + ex.Message;
        }
    }

    [RelayCommand]
    public void LoadChannel()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        if (string.IsNullOrWhiteSpace(ActiveSolutionId)) { Status = "请先选择产品方案。"; return; }
        try
        {
            var view = manager.ReadChannel(ActiveSolutionId, SelectedChannelId);
            draft = manager.Repository.GetDraft(ActiveSolutionId, view.StationId);
            StationId = view.StationId;
            StationRevision = view.StationRevision;
            BrightThreshold = view.StationTunables.TryGetValue("brightThreshold", out var t) ? t : 127;
            MinimumBrightFraction = view.StationTunables.TryGetValue("minimumBrightFraction", out var f) ? f : 0.1;
            Status = "已读取草稿 " + view.ChannelId + " @ " + view.StationId +
                     " revision=" + view.StationRevision + "（草稿，未发布）";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException or ArgumentException)
        {
            draft = null;
            Status = "读取草稿失败：" + ex.Message;
        }
    }

    private StationRecipe BuildEditedRecipe()
    {
        if (draft is null) throw new InvalidOperationException("请先读取一个通道草稿。");
        var tunables = new Dictionary<string, double>(draft.Recipe.Tunables, StringComparer.Ordinal)
        {
            ["brightThreshold"] = BrightThreshold,
            ["minimumBrightFraction"] = MinimumBrightFraction
        };
        return draft.Recipe with { Tunables = tunables };
    }

    [RelayCommand]
    public void Validate()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        try
        {
            var recipe = BuildEditedRecipe();
            recipe.Validate();
            RoiReleaseWorkflow.ValidateStationRois(recipe);
            Status = "校验通过：参数与 ROI 合法（尚未保存）。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            Status = "校验失败，未写入：" + ex.Message;
        }
    }

    [RelayCommand]
    public void SaveDraft()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        try
        {
            var saved = manager.SaveStation(ActiveSolutionId, BuildEditedRecipe(), StationRevision);
            draft = saved;
            StationRevision = saved.Revision;
            Status = "草稿已保存 revision=" + saved.Revision + "（仍未发布，不影响 Active）。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            Status = "保存草稿被拒绝（可能是版本冲突）：" + ex.Message;
        }
    }

    [RelayCommand]
    public void Publish()
    {
        if (manager is null) { Status = "未启用离线方案管理。"; return; }
        var result = manager.Publish(ActiveSolutionId);
        if (result.Outcome != OfflinePublishOutcome.Published || result.Release is null)
        {
            PublishedRelease = "未发布";
            PublishedSha = "";
            Status = "发布被拒绝：" + result.Message;
            return;
        }
        PublishedRelease = result.Release.ReleaseId;
        PublishedSha = result.Release.ContentSha256;
        ActivationState = "已发布但未激活：双机激活需要 #149 门禁。";
        Status = "已发布不可变 Release：" + result.Release.ReleaseId;
    }
}
