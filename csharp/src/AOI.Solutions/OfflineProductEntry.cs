namespace AOI.Solutions;

/// <summary>
/// Explicit OFFLINE_RECIPE_MANAGEMENT entry: either the A6 solutions.sqlite itself or
/// the A6 run-root directory that contains it. Anything else fails closed; this never
/// creates a database or a directory.
/// </summary>
public static class OfflineProductEntry
{
    public const string DatabaseFileName = "solutions.sqlite";

    public static string ResolveSolutionsDatabase(string pathOrRunRoot)
    {
        if (string.IsNullOrWhiteSpace(pathOrRunRoot))
            throw new ArgumentException("需要提供 A6 solutions.sqlite 或 run-root 路径。",
                nameof(pathOrRunRoot));
        string full = Path.GetFullPath(pathOrRunRoot);
        if (Directory.Exists(full)) return Path.Combine(full, DatabaseFileName);
        if (File.Exists(full))
        {
            if (!string.Equals(Path.GetFileName(full), DatabaseFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("只接受 A6 solutions.sqlite，拒绝其他数据库：" +
                    Path.GetFileName(full));
            return full;
        }
        throw new FileNotFoundException("A6 solutions.sqlite / run-root 不存在；不会创建新数据库。", full);
    }
}
