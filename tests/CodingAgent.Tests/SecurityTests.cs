using CodingAgent.Security;
using Xunit;

namespace CodingAgent.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void Resolve_AllowsWorkspaceChild()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var policy = new WorkspacePathPolicy(root);

        var result = policy.Resolve("src/Program.cs");

        Assert.StartsWith(Path.GetFullPath(root), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var policy = new WorkspacePathPolicy(root);

        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve("../secret.txt"));
    }

    [Fact]
    public void Resolve_RejectsExistingReparsePoint()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        var link = Path.Combine(root, "outside");

        try
        {
            Directory.CreateSymbolicLink(link, target);
            var policy = new WorkspacePathPolicy(root);

            Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve("outside/secret.txt"));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Theory]
    [InlineData("git", "--force")]
    [InlineData("powershell", "Get-ChildItem")]
    public void CommandPolicy_RejectsDangerousCommands(string executable, string argument)
    {
        var policy = new CommandPolicy();

        Assert.Throws<UnauthorizedAccessException>(() => policy.EnsureAllowed(executable, [argument]));
    }

    [Fact]
    public void CommandPolicy_AllowsAgentBranchPush()
    {
        var policy = new CommandPolicy();

        policy.EnsureAllowed("git", ["push", "origin", "--set-upstream", "HEAD:refs/heads/agent/add-tests"]);
    }

    [Theory]
    [InlineData("HEAD:refs/heads/main")]
    [InlineData("HEAD:refs/heads/feature/not-agent")]
    [InlineData("HEAD:refs/heads/agent/bad branch")]
    [InlineData("HEAD:refs/heads/agent/../main")]
    [InlineData("HEAD:refs/heads/agent//main")]
    public void CommandPolicy_RejectsUnsafePushTarget(string refspec)
    {
        var policy = new CommandPolicy();

        Assert.Throws<UnauthorizedAccessException>(() =>
            policy.EnsureAllowed("git", ["push", "origin", "--set-upstream", refspec]));
    }
}