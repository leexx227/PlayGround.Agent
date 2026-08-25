using CodingAgent.Core.Execution;
using CodingAgent.Repositories;
using Xunit;

namespace CodingAgent.Tests;

public sealed class BranchPublishProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_PushesOnlyAgentBranchWithCredentialsInEnvironment(bool github)
    {
        var tokenVariable = github ? "GITHUB_TOKEN" : "AZURE_DEVOPS_TOKEN";
        var previousToken = Environment.GetEnvironmentVariable(tokenVariable);
        Environment.SetEnvironmentVariable(tokenVariable, "test-token");
        try
        {
            var runner = new RecordingProcessRunner();
            var provider = github
                ? new GitHubRepositoryProvider(runner)
                : (GitRepositoryProvider)new AzureDevOpsRepositoryProvider(runner);

            await provider.PushBranchAsync("/workspace", "agent/add-tests", TestContext.Current.CancellationToken);

            Assert.Equal(["push", "origin", "--set-upstream", "HEAD:refs/heads/agent/add-tests"], runner.LastRequest!.Arguments);
            Assert.Equal("0", runner.LastRequest.EnvironmentVariables!["GIT_TERMINAL_PROMPT"]);
            Assert.Contains("Authorization:", runner.LastRequest.EnvironmentVariables["GIT_CONFIG_VALUE_0"], StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, previousToken);
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
}