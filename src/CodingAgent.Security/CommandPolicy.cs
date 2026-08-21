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
        "push",
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
    }
}