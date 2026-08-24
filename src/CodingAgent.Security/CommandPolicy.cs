namespace CodingAgent.Security;

public sealed class CommandPolicy
{
    private static readonly HashSet<string> AllowedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet",
        "git",
    };

    private static readonly string[] ForbiddenArguments =
    [
        "--force",
        "--force-with-lease",
        "credential",
        "config",
    ];

    public void EnsureAllowed(string executable, IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        if (!AllowedExecutables.Contains(name))
        {
            throw new UnauthorizedAccessException($"Executable '{name}' is not allowed.");
        }

        if (name.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            arguments.Any(argument => ForbiddenArguments.Contains(argument, StringComparer.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException("This Git operation requires explicit approval and is not available to the agent.");
        }

        if (name.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            arguments.FirstOrDefault()?.Equals("push", StringComparison.OrdinalIgnoreCase) == true)
        {
            EnsureSafePush(arguments);
        }
    }

    private static void EnsureSafePush(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4 ||
            !arguments[1].Equals("origin", StringComparison.Ordinal) ||
            !arguments[2].Equals("--set-upstream", StringComparison.Ordinal) ||
            !arguments[3].StartsWith("HEAD:refs/heads/", StringComparison.Ordinal) ||
            !GitBranchPolicy.IsAgentBranch(arguments[3]["HEAD:refs/heads/".Length..]))
        {
            throw new UnauthorizedAccessException("Git push is restricted to the current HEAD and an agent/* remote branch.");
        }
    }
}

public static class GitBranchPolicy
{
    public static bool IsAgentBranch(string branch) =>
        branch.StartsWith("agent/", StringComparison.Ordinal) && IsValid(branch);

    public static bool IsValid(string branch) =>
        !string.IsNullOrWhiteSpace(branch) &&
        !branch.StartsWith("-", StringComparison.Ordinal) &&
        !branch.EndsWith("/", StringComparison.Ordinal) &&
        !branch.EndsWith(".", StringComparison.Ordinal) &&
        !branch.Contains("..", StringComparison.Ordinal) &&
        !branch.Contains("//", StringComparison.Ordinal) &&
        !branch.Contains("@{", StringComparison.Ordinal) &&
        branch.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/');
}