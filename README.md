# Coding Agent

A .NET 10 Microsoft Foundry Hosted Agent that works on C# repositories from GitHub and Azure DevOps. It clones a repository into an isolated session workspace, edits and validates it locally, and returns the Git diff. After explicit human tool approval, it can commit and push an `agent/*` branch. A connected Foundry Toolbox provides GitHub and Azure DevOps MCP tools for creating and managing pull requests. Direct pushes to `main` or any non-agent branch are rejected.

See [the design](docs/DESIGN.md) for architecture, security boundaries, deployment, and the delivery roadmap.

## Build and test

```powershell
dotnet build CodingAgent.slnx --configuration Release
dotnet test CodingAgent.slnx --configuration Release --no-build
```

Build the deployment image from the repository root:

```powershell
docker build --tag coding-agent:local .
```

## Run locally

Copy `.env.example` to `.env`, provide a Foundry project endpoint and model deployment, authenticate with Azure CLI, and run:

```powershell
dotnet run --project src/CodingAgent.Host
```

The Responses endpoint listens on `http://localhost:8088/responses`. A non-streaming request is:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://localhost:8088/responses `
  -ContentType application/json `
  -Body '{"input":"Open https://github.com/contoso/orders-api and inspect the solution.","stream":false}'
```

For Agent Inspector and deployment, install Azure Developer CLI 1.27.1 or later and the Foundry extension, then use `azd ai agent run`, `azd provision`, and `azd deploy`.

To deploy a prebuilt image with the REST helper, pass environment-specific values explicitly. Do not commit these values:

```powershell
.\Deploy.ps1 `
  -Image "<registry>.azurecr.io/coding-agent:<tag>" `
  -ProjectEndpoint "https://<account>.services.ai.azure.com/api/projects/<project>" `
  -ModelDeployment "<model-deployment-name>" `
  -AgentName "<agent-name>" `
  -ToolboxName "<toolbox-name>"
```

`ModelDeployment` is the deployment name configured in the target Foundry resource, not the underlying model ID. Pass it explicitly even when the deployment name matches the model name.

## Repository credentials and Toolbox

The remote MCP servers run outside the Hosted Agent container and cannot access `$HOME/workspaces/current`. Therefore local Git remains responsible for clone and push, while Toolbox MCP tools create and manage pull requests. The local branch publication function and Toolbox write tools must require human approval.

Configure `TOOLBOX_NAME` with the name of a healthy Toolbox default version. Complete OAuth consent for every tool source before deployment; one failing source causes Toolbox `tools/list` and Hosted Agent readiness to fail. Prefer a dedicated Toolbox containing only the repository tools required by this Agent.

Private clone and local push require credentials inside the Agent container. Configure them as Microsoft Foundry project connections; never put tokens in source, image URLs, command arguments, or Git configuration files.

For GitHub, create a `CustomKeys` connection with a secret field named `github_token`. Use a GitHub App installation token or a fine-grained token scoped to the target repositories with Contents and Pull requests read/write permissions. Deploy with:

```powershell
.\Deploy.ps1 `
  -Image "<registry>.azurecr.io/coding-agent:<tag>" `
  -ProjectEndpoint "https://<account>.services.ai.azure.com/api/projects/<project>" `
  -ModelDeployment "<model-deployment-name>" `
  -AgentName "<agent-name>" `
  -ToolboxName "<toolbox-name>" `
  -GitHubConnectionName "<github-connection-name>"
```

For Azure DevOps, create a `CustomKeys` connection with a secret field named `ado_token`. A PAT requires Code read/write and uses the default `Basic` scheme. An Entra access token uses `-AzureDevOpsAuthScheme Bearer`.

```powershell
.\Deploy.ps1 `
  -Image "<registry>.azurecr.io/coding-agent:<tag>" `
  -ProjectEndpoint "https://<account>.services.ai.azure.com/api/projects/<project>" `
  -ModelDeployment "<model-deployment-name>" `
  -AgentName "<agent-name>" `
  -ToolboxName "<toolbox-name>" `
  -AzureDevOpsConnectionName "<ado-connection-name>"
```

Provide both connection parameters when one Agent must clone and push to both providers. Public repository inspection works without these connections; branch publication fails closed when the corresponding token isn't configured. The Toolbox manages its own downstream GitHub/ADO authentication separately.