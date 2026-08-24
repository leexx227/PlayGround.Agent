namespace CodingAgent.Core.Repositories;

public enum RepositoryProviderKind
{
    GitHub,
    AzureDevOps,
}

public sealed record RepositoryReference(
    RepositoryProviderKind Provider,
    Uri CloneUri,
    string RepositoryName,
    string Revision = "main",
    string? Organization = null,
    string? Project = null);

public sealed record PullRequestRequest(
    RepositoryReference Repository,
    string Workspace,
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description);

public sealed record PullRequestResult(Uri Url, string Id);

public interface IRepositoryProvider
{
    RepositoryProviderKind Kind { get; }

    bool TryParse(Uri repositoryUri, string revision, out RepositoryReference? reference);

    Task CloneAsync(RepositoryReference reference, string destination, CancellationToken cancellationToken);

    Task<PullRequestResult> PublishPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken);
}