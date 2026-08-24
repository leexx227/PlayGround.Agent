using System.Net;
using System.Text;
using CodingAgent.Core.Execution;
using CodingAgent.Core.Repositories;
using CodingAgent.Repositories;
using Xunit;

namespace CodingAgent.Tests;

public sealed class PullRequestProviderTests
{
    [Fact]
    public async Task GitHubProvider_PushesAgentBranchAndCreatesPullRequest()
    {
        const string token = "test-github-token";
        var previousToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", token);
        try
        {
            var runner = new RecordingProcessRunner();
            var handler = new RecordingHttpHandler("""{"number":42,"html_url":"https://github.com/contoso/orders/pull/42"}""");
            var provider = new GitHubRepositoryProvider(runner, new HttpClient(handler));
            var repository = new RepositoryReference(
                RepositoryProviderKind.GitHub,
                new Uri("https://github.com/contoso/orders"),
                "orders",
                "main",
                "contoso");

            var result = await provider.PublishPullRequestAsync(
                new PullRequestRequest(repository, "/workspace", "agent/add-tests", "main", "Add tests", "Summary"),
                TestContext.Current.CancellationToken);

            Assert.Equal("42", result.Id);
            Assert.Equal("https://github.com/contoso/orders/pull/42", result.Url.AbsoluteUri);
            Assert.Equal(["push", "origin", "--set-upstream", "HEAD:refs/heads/agent/add-tests"], runner.LastRequest!.Arguments);
            Assert.Equal("Authorization: Bearer test-github-token", runner.LastRequest.EnvironmentVariables!["GIT_CONFIG_VALUE_0"]);
            Assert.Equal("https://api.github.com/repos/contoso/orders/pulls", handler.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-github-token", handler.Authorization);
            Assert.Contains("\"head\":\"agent/add-tests\"", handler.Content, StringComparison.Ordinal);
            Assert.Contains("\"base\":\"main\"", handler.Content, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousToken);
        }
    }

    [Fact]
    public async Task AzureDevOpsProvider_PushesAgentBranchAndCreatesPullRequest()
    {
        const string token = "test-ado-token";
        var previousToken = Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN");
        var previousScheme = Environment.GetEnvironmentVariable("AZURE_DEVOPS_AUTH_SCHEME");
        Environment.SetEnvironmentVariable("AZURE_DEVOPS_TOKEN", token);
        Environment.SetEnvironmentVariable("AZURE_DEVOPS_AUTH_SCHEME", "Basic");
        try
        {
            var runner = new RecordingProcessRunner();
            var handler = new RecordingHttpHandler("""{"pullRequestId":17}""");
            var provider = new AzureDevOpsRepositoryProvider(runner, new HttpClient(handler));
            var repository = new RepositoryReference(
                RepositoryProviderKind.AzureDevOps,
                new Uri("https://dev.azure.com/contoso/Commerce/_git/orders"),
                "orders",
                "main",
                "contoso",
                "Commerce");

            var result = await provider.PublishPullRequestAsync(
                new PullRequestRequest(repository, "/workspace", "agent/add-tests", "main", "Add tests", "Summary"),
                TestContext.Current.CancellationToken);

            Assert.Equal("17", result.Id);
            Assert.Equal("https://dev.azure.com/contoso/Commerce/_git/orders/pullrequest/17", result.Url.AbsoluteUri);
            Assert.Equal("https://dev.azure.com/contoso/Commerce/_apis/git/repositories/orders/pullrequests?api-version=7.1", handler.RequestUri!.AbsoluteUri);
            Assert.StartsWith("Basic ", handler.Authorization, StringComparison.Ordinal);
            Assert.Contains("\"sourceRefName\":\"refs/heads/agent/add-tests\"", handler.Content, StringComparison.Ordinal);
            Assert.Contains("\"targetRefName\":\"refs/heads/main\"", handler.Content, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_DEVOPS_TOKEN", previousToken);
            Environment.SetEnvironmentVariable("AZURE_DEVOPS_AUTH_SCHEME", previousScheme);
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false));
        }
    }

    private sealed class RecordingHttpHandler(string responseContent) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string Content { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json"),
            };
        }
    }
}