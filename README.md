# Coding Agent

A .NET 10 Microsoft Foundry Hosted Agent that works on C# repositories from GitHub and Azure DevOps. It can clone a repository into an isolated session workspace, inspect and edit files, create a local agent branch, run `dotnet restore/build/test`, and return the Git diff. Push and pull-request operations are intentionally excluded until an approval and identity layer is configured.

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
  -AgentName "<agent-name>"
```

The model deployment defaults to `gpt-5.6-sol`, the quality-first GPT-5.6 model for advanced coding and agentic workflows. Override it with `-ModelDeployment` when the target project uses a different deployment name.