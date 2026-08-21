namespace CodingAgent.Security;

public sealed class WorkspacePathPolicy(string workspaceRoot)
{
    public string WorkspaceRoot { get; } = Path.GetFullPath(workspaceRoot);

    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var resolved = Path.GetFullPath(relativePath, WorkspaceRoot);
        var relative = Path.GetRelativePath(WorkspaceRoot, resolved);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The requested path is outside the workspace.");
        }

        EnsureNoReparsePoint(relative);

        return resolved;
    }

    private void EnsureNoReparsePoint(string relativePath)
    {
        var current = WorkspaceRoot;
        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Symbolic links and reparse points are not allowed in workspace paths.");
            }
        }
    }
}