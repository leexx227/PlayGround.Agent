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
    string Revision = "main");

public interface IRepositoryProvider
{
    RepositoryProviderKind Kind { get; }

    bool TryParse(Uri repositoryUri, string revision, out RepositoryReference? reference);

    Task CloneAsync(RepositoryReference reference, string destination, CancellationToken cancellationToken);
}