using CodingAgent.Core.Execution;
using CodingAgent.Core.Repositories;

namespace CodingAgent.Repositories;

public abstract class GitRepositoryProvider(IProcessRunner processRunner) : IRepositoryProvider
{
    public abstract RepositoryProviderKind Kind { get; }

    public abstract bool TryParse(Uri repositoryUri, string revision, out RepositoryReference? reference);

    public async Task CloneAsync(RepositoryReference reference, string destination, CancellationToken cancellationToken)
    {
        if (reference.Provider != Kind)
        {
            throw new ArgumentException($"Provider {Kind} cannot clone {reference.Provider} repositories.", nameof(reference));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var result = await processRunner.RunAsync(
            new ProcessRequest(
                "git",
                ["clone", "--depth", "1", "--branch", reference.Revision, "--", reference.CloneUri.AbsoluteUri, destination],
                Path.GetDirectoryName(destination)!,
                TimeSpan.FromMinutes(5)),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Repository clone failed: {result.StandardError}");
        }
    }
}

public sealed class GitHubRepositoryProvider(IProcessRunner processRunner) : GitRepositoryProvider(processRunner)
{
    public override RepositoryProviderKind Kind => RepositoryProviderKind.GitHub;

    public override bool TryParse(Uri repositoryUri, string revision, out RepositoryReference? reference)
    {
        reference = null;
        if (!repositoryUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !repositoryUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = repositoryUri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2)
        {
            return false;
        }

        var repositoryName = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        reference = new RepositoryReference(Kind, repositoryUri, repositoryName, revision);
        return true;
    }
}

public sealed class AzureDevOpsRepositoryProvider(IProcessRunner processRunner) : GitRepositoryProvider(processRunner)
{
    public override RepositoryProviderKind Kind => RepositoryProviderKind.AzureDevOps;

    public override bool TryParse(Uri repositoryUri, string revision, out RepositoryReference? reference)
    {
        reference = null;
        var isModern = repositoryUri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase);
        var isLegacy = repositoryUri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
        if (!repositoryUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || (!isModern && !isLegacy))
        {
            return false;
        }

        var segments = repositoryUri.AbsolutePath.Trim('/').Split('/');
        var gitIndex = Array.FindIndex(segments, segment => segment.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (gitIndex < 1 || gitIndex + 1 >= segments.Length)
        {
            return false;
        }

        reference = new RepositoryReference(Kind, repositoryUri, segments[gitIndex + 1], revision);
        return true;
    }
}