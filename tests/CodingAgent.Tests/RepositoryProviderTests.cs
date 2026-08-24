using CodingAgent.Core.Execution;
using CodingAgent.Core.Repositories;
using CodingAgent.Repositories;
using Xunit;

namespace CodingAgent.Tests;

public sealed class RepositoryProviderTests
{
    private readonly IProcessRunner processRunner = new StubProcessRunner();

    [Fact]
    public void GitHubProvider_ParsesCanonicalUrl()
    {
        var provider = new GitHubRepositoryProvider(processRunner);

        var parsed = provider.TryParse(new Uri("https://github.com/contoso/orders-api.git"), "develop", out var reference);

        Assert.True(parsed);
        Assert.Equal(RepositoryProviderKind.GitHub, reference!.Provider);
        Assert.Equal("orders-api", reference.RepositoryName);
        Assert.Equal("develop", reference.Revision);
        Assert.Equal("contoso", reference.Organization);
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Commerce/_git/payment-service")]
    [InlineData("https://contoso.visualstudio.com/Commerce/_git/payment-service")]
    public void AzureDevOpsProvider_ParsesSupportedUrls(string url)
    {
        var provider = new AzureDevOpsRepositoryProvider(processRunner);

        var parsed = provider.TryParse(new Uri(url), "main", out var reference);

        Assert.True(parsed);
        Assert.Equal(RepositoryProviderKind.AzureDevOps, reference!.Provider);
        Assert.Equal("payment-service", reference.RepositoryName);
        Assert.Equal("contoso", reference.Organization);
        Assert.Equal("Commerce", reference.Project);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false));
    }
}