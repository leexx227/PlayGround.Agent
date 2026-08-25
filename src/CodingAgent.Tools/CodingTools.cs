using System.ComponentModel;
using System.Text.RegularExpressions;
using CodingAgent.Core.Execution;
using CodingAgent.Core.Repositories;
using CodingAgent.Security;

namespace CodingAgent.Tools;

public sealed class CodingTools(
    WorkspacePathPolicy paths,
    IProcessRunner processRunner,
    IReadOnlyList<IRepositoryProvider> repositoryProviders)
{
    private static readonly string[] IgnoredDirectories = [".git", "bin", "obj"];
    private RepositoryReference? repositoryReference;

    [Description("Clone a GitHub or Azure DevOps repository into the isolated session workspace. Call this before other tools.")]
    public async Task<string> OpenRepositoryAsync(
        [Description("HTTPS GitHub or Azure DevOps repository URL")] string repositoryUrl,
        [Description("Branch or tag to open, usually main")] string revision = "main",
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(repositoryUrl, UriKind.Absolute);
        if (!GitBranchPolicy.IsValid(revision))
        {
            throw new ArgumentException("Revision must be a safe branch or tag name.", nameof(revision));
        }

        var provider = repositoryProviders.FirstOrDefault(candidate => candidate.TryParse(uri, revision, out _))
            ?? throw new ArgumentException("Only canonical HTTPS GitHub and Azure DevOps repository URLs are supported.", nameof(repositoryUrl));
        provider.TryParse(uri, revision, out var reference);

        if (Directory.Exists(paths.WorkspaceRoot))
        {
            Directory.Delete(paths.WorkspaceRoot, recursive: true);
        }

        await provider.CloneAsync(reference!, paths.WorkspaceRoot, cancellationToken);
        repositoryReference = reference;
        return $"Opened {reference!.Provider} repository {reference.RepositoryName} at {revision}.";
    }

    [Description("List files in a workspace directory. Build outputs and Git internals are omitted.")]
    public string ListFiles([Description("Workspace-relative directory, or . for the root")] string directory = ".")
    {
        var root = paths.Resolve(directory);
        EnsureExistingDirectory(root);
        return string.Join('\n', Directory.EnumerateFileSystemEntries(root)
            .Where(path => !IgnoredDirectories.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(paths.WorkspaceRoot, path).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase));
    }

    [Description("Read a UTF-8 text file from the workspace with optional line bounds.")]
    public string ReadFile(
        [Description("Workspace-relative file path")] string path,
        [Description("First line, one-based")] int startLine = 1,
        [Description("Maximum number of lines to return")] int lineCount = 400)
    {
        var resolved = paths.Resolve(path);
        if (startLine < 1 || lineCount is < 1 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(startLine), "Line bounds are invalid.");
        }

        return string.Join('\n', File.ReadLines(resolved).Skip(startLine - 1).Take(lineCount));
    }

    [Description("Search text files in the workspace using a regular expression.")]
    public string Search(
        [Description("Regular expression to find")] string pattern,
        [Description("Optional file suffix such as .cs")] string? fileSuffix = null)
    {
        var expression = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        var matches = new List<string>();
        foreach (var file in EnumerateWorkspaceFiles(fileSuffix))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (expression.IsMatch(line))
                {
                    matches.Add($"{Path.GetRelativePath(paths.WorkspaceRoot, file).Replace('\\', '/')}:{lineNumber}:{line.Trim()}");
                    if (matches.Count == 200)
                    {
                        return string.Join('\n', matches);
                    }
                }
            }
        }

        return string.Join('\n', matches);
    }

    [Description("Create or replace a UTF-8 text file in the workspace. Use only after reading nearby code.")]
    public async Task<string> WriteFileAsync(
        [Description("Workspace-relative file path")] string path,
        [Description("Complete new file content")] string content,
        CancellationToken cancellationToken = default)
    {
        var resolved = paths.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        await File.WriteAllTextAsync(resolved, content, cancellationToken);
        return $"Wrote {path} ({content.Length} characters).";
    }

    [Description("Return Git status and the current unified diff without changing the repository.")]
    public async Task<string> GetChangesAsync(CancellationToken cancellationToken = default)
    {
        EnsureRepositoryIsOpen();
        var status = await RunAsync("git", ["status", "--short"], TimeSpan.FromSeconds(30), cancellationToken);
        var diff = await RunAsync("git", ["diff", "--", "."], TimeSpan.FromSeconds(30), cancellationToken);
        return $"STATUS\n{status.StandardOutput}\nDIFF\n{diff.StandardOutput}";
    }

    [Description("Create and switch to a local working branch. This does not push any changes.")]
    public async Task<string> CreateBranchAsync(
        [Description("Branch name, which must start with agent/")] string branchName,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryIsOpen();
        if (!GitBranchPolicy.IsAgentBranch(branchName))
        {
            throw new ArgumentException("Agent branches must start with 'agent/' and contain no whitespace.", nameof(branchName));
        }

        var result = await RunAsync("git", ["switch", "-c", branchName], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSucceeded(result, "Branch creation");
        return $"Created local branch {branchName}. Nothing was pushed.";
    }

    [Description("Run restore, Release build, and tests for a .sln, .slnx, or .csproj target and return all results.")]
    public async Task<string> ValidateDotNetAsync(
        [Description("Workspace-relative .sln, .slnx, or .csproj path")] string target,
        CancellationToken cancellationToken = default)
    {
        var resolved = paths.Resolve(target);
        if (!File.Exists(resolved) || !new[] { ".sln", ".slnx", ".csproj" }.Contains(Path.GetExtension(resolved), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Validation target must be an existing .sln, .slnx, or .csproj file.", nameof(target));
        }

        var restore = await RunAsync("dotnet", ["restore", resolved], TimeSpan.FromMinutes(5), cancellationToken);
        if (!restore.Succeeded)
        {
            return FormatValidation("restore", restore);
        }

        var build = await RunAsync("dotnet", ["build", resolved, "--no-restore", "--configuration", "Release"], TimeSpan.FromMinutes(10), cancellationToken);
        if (!build.Succeeded)
        {
            return $"{FormatValidation("restore", restore)}\n{FormatValidation("build", build)}";
        }

        var resultsDirectory = Path.Combine(Path.GetTempPath(), "coding-agent-test-results", Guid.NewGuid().ToString("N"));
        var test = await RunAsync(
            "dotnet",
            ["test", resolved, "--no-build", "--configuration", "Release", "--logger", "trx", "--results-directory", resultsDirectory],
            TimeSpan.FromMinutes(10),
            cancellationToken);
        return $"{FormatValidation("restore", restore)}\n{FormatValidation("build", build)}\n{FormatValidation("test", test)}";
    }

    [Description("Validate, commit, and push the current agent/* branch. Call only when the user explicitly approves publishing changes, then use the Foundry Toolbox to create the pull request.")]
    public async Task<string> PublishBranchAsync(
        [Description("Workspace-relative .sln, .slnx, or .csproj to validate before publishing")] string validationTarget,
        [Description("Concise commit subject")] string commitMessage,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryIsOpen();
        var reference = repositoryReference
            ?? throw new InvalidOperationException("Repository metadata is unavailable. Open the repository again before creating a pull request.");
        ValidateCommitMessage(commitMessage);

        var branchResult = await RunAsync("git", ["branch", "--show-current"], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSucceeded(branchResult, "Current branch lookup");
        var sourceBranch = branchResult.StandardOutput.Trim();
        if (!GitBranchPolicy.IsAgentBranch(sourceBranch))
        {
            throw new InvalidOperationException("Pull requests can only be published from an agent/* branch.");
        }

        if (sourceBranch.Equals(reference.Revision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The source and target branches must be different.");
        }

        var validation = await ValidateDotNetAsync(validationTarget, cancellationToken);
        if (validation.Contains("FAILED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Branch publication stopped because validation failed.\n{validation}");
        }

        var add = await RunAsync("git", ["add", "--all"], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSucceeded(add, "Staging changes");
        var staged = await RunAsync("git", ["diff", "--cached", "--quiet"], TimeSpan.FromSeconds(30), cancellationToken);
        if (staged.ExitCode == 0)
        {
            throw new InvalidOperationException("There are no changes to publish.");
        }

        if (staged.ExitCode != 1 || staged.TimedOut)
        {
            EnsureSucceeded(staged, "Staged change check");
        }

        var commit = await RunAsync(
            "git",
            ["-c", "user.name=Microsoft Foundry Coding Agent", "-c", "user.email=coding-agent@users.noreply.github.com", "commit", "-m", commitMessage],
            TimeSpan.FromMinutes(2),
            cancellationToken);
        EnsureSucceeded(commit, "Commit");

        var provider = repositoryProviders.Single(candidate => candidate.Kind == reference.Provider);
        await provider.PushBranchAsync(paths.WorkspaceRoot, sourceBranch, cancellationToken);

        return $"Published {sourceBranch} to {reference.Provider}. Use the Foundry Toolbox repository tool to create a pull request targeting {reference.Revision}.\n\n{validation}";
    }

    private IEnumerable<string> EnumerateWorkspaceFiles(string? suffix) =>
        Directory.EnumerateFiles(paths.WorkspaceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(paths.WorkspaceRoot, path).Split(Path.DirectorySeparatorChar)
                .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Where(path => suffix is null || path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        processRunner.RunAsync(new ProcessRequest(executable, arguments, paths.WorkspaceRoot, timeout), cancellationToken);

    private static string FormatValidation(string phase, ProcessResult result) =>
        $"{phase.ToUpperInvariant()}: {(result.Succeeded ? "PASSED" : "FAILED")} (exit {result.ExitCode})\n{result.StandardOutput}\n{result.StandardError}";

    private static void EnsureSucceeded(ProcessResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{operation} failed: {result.StandardError}");
        }
    }

    private void EnsureRepositoryIsOpen()
    {
        if (!Directory.Exists(Path.Combine(paths.WorkspaceRoot, ".git")))
        {
            throw new InvalidOperationException("Open a repository before using Git tools.");
        }
    }

    private static void EnsureExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }
    }

    private static void ValidateCommitMessage(string commitMessage)
    {
        if (string.IsNullOrWhiteSpace(commitMessage) || commitMessage.Length > 200 || commitMessage.Contains('\n') || commitMessage.Contains('\r'))
        {
            throw new ArgumentException("The commit message must be a single non-empty line of at most 200 characters.", nameof(commitMessage));
        }
    }
}