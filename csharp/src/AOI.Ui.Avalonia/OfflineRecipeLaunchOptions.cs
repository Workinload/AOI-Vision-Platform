namespace AOI.Ui.Avalonia;

/// <summary>
/// Explicit OFFLINE_RECIPE_MANAGEMENT startup entry. It is only enabled when BOTH an A6
/// solutions.sqlite/run-root and an operator are provided; a partial or malformed entry is
/// rejected instead of silently opening a database.
/// </summary>
public sealed record OfflineRecipeLaunchOptions(
    bool Requested, string EntryPath, string OperatorId, string? Error)
{
    public static OfflineRecipeLaunchOptions None { get; } = new(false, "", "", null);

    public static OfflineRecipeLaunchOptions Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0) return None;
        bool requested = false;
        string? entry = null;
        string? actor = null;
        string? error = null;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i] ?? "";
            if (string.Equals(arg, "--offline-recipe-management", StringComparison.Ordinal))
            {
                requested = true;
                if (i + 1 < args.Count && !StartsWithFlag(args[i + 1]))
                    entry = args[++i];
                else
                    error ??= "缺少 --offline-recipe-management <solutions.sqlite|run-root> 路径。";
            }
            else if (arg.StartsWith("--offline-recipe-management=", StringComparison.Ordinal))
            {
                requested = true;
                entry = arg["--offline-recipe-management=".Length..];
            }
            else if (string.Equals(arg, "--operator", StringComparison.Ordinal))
            {
                if (i + 1 < args.Count && !StartsWithFlag(args[i + 1]))
                    actor = args[++i];
                else
                    error ??= "缺少 --operator <操作员>。";
            }
            else if (arg.StartsWith("--operator=", StringComparison.Ordinal))
            {
                actor = arg["--operator=".Length..];
            }
        }
        if (!requested) return None;
        if (string.IsNullOrWhiteSpace(entry))
            error ??= "缺少 A6 solutions.sqlite 或 run-root 路径。";
        if (string.IsNullOrWhiteSpace(actor))
            error ??= "OFFLINE_RECIPE_MANAGEMENT 需要显式 --operator <操作员>；已拒绝启用。";
        return new(true, entry ?? "", actor ?? "", error);
    }

    private static bool StartsWithFlag(string? value) =>
        value is not null && value.StartsWith("--", StringComparison.Ordinal);
}
