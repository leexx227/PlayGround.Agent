using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using CodingAgent.Core.Execution;
using CodingAgent.Core.Repositories;
using CodingAgent.Repositories;
using CodingAgent.Security;
using CodingAgent.Tools;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

Env.NoClobber().TraversePath().Load();

var projectEndpoint = new Uri(
    Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set."));
var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME is not set.");
var workspaceRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "workspaces",
    "current");
var pathPolicy = new WorkspacePathPolicy(workspaceRoot);
IProcessRunner processRunner = new RestrictedProcessRunner(new CommandPolicy());
IReadOnlyList<IRepositoryProvider> repositoryProviders =
[
    new GitHubRepositoryProvider(processRunner),
    new AzureDevOpsRepositoryProvider(processRunner),
];
var codingTools = new CodingTools(pathPolicy, processRunner, repositoryProviders);
AIFunction createPullRequestFunction = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create(codingTools.CreatePullRequestAsync));

const string AgentInstructions = """
    You are a coding agent specialized in C# repositories hosted on GitHub and Azure DevOps.
    Always open the repository first, inspect its instructions and nearby code, and state a short plan.
    Keep changes focused. Read a file before replacing it. Add or update tests for behavioral changes.
    Run the narrowest relevant validation, then run restore, Release build, and tests before finishing.
    If validation fails, diagnose and repair the same slice. Do not claim success when a command failed.
    Never request credentials or access paths outside the workspace. Never push directly to a protected or non-agent branch.
    Create a pull request only when the user explicitly asks for it. Before creating one, use an agent/* branch and require passing restore, Release build, and tests.
    Finish with changed files, validation results, the diff summary, and remaining risks.
    """;

AIAgent agent = new AIProjectClient(projectEndpoint, new DefaultAzureCredential())
    .AsAIAgent(
        model: deployment,
        instructions: AgentInstructions,
        name: "coding-agent",
        description: "A C# coding agent for GitHub and Azure DevOps repositories.",
        tools:
        [
            AIFunctionFactory.Create(codingTools.OpenRepositoryAsync),
            AIFunctionFactory.Create(codingTools.ListFiles),
            AIFunctionFactory.Create(codingTools.ReadFile),
            AIFunctionFactory.Create(codingTools.Search),
            AIFunctionFactory.Create(codingTools.WriteFileAsync),
            AIFunctionFactory.Create(codingTools.GetChangesAsync),
            AIFunctionFactory.Create(codingTools.CreateBranchAsync),
            AIFunctionFactory.Create(codingTools.ValidateDotNetAsync),
            createPullRequestFunction,
        ]);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();