param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Image,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectEndpoint,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ModelDeployment,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AgentName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]*$')]
    [string]$ToolboxName,

    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]*$')]
    [string]$GitHubConnectionName,

    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]*$')]
    [string]$AzureDevOpsConnectionName,

    [ValidateSet("Basic", "Bearer")]
    [string]$AzureDevOpsAuthScheme = "Basic"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Image -notmatch '^[a-zA-Z0-9.-]+(?::[0-9]+)?/[a-z0-9._/-]+(?::[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}|@sha256:[a-fA-F0-9]{64})$') {
    throw "Invalid container image '$Image'. Expected '<registry>/<repository>:<tag>' or '<registry>/<repository>@sha256:<digest>'."
}

Write-Host "Deploying image: $Image"
Write-Host "Foundry project: $ProjectEndpoint"

$Token = az account get-access-token `
  --resource https://ai.azure.com `
  --query accessToken `
  --output tsv

$Headers = @{
    Authorization = "Bearer $Token"
    "Content-Type" = "application/json"
}

$Definition = @{
        kind = "hosted"
        container_configuration = @{
                image = $Image
        }
        cpu = "2"
        memory = "4Gi"
        protocol_versions = @(
                @{
                        protocol = "responses"
                        version = "2.0.0"
                }
        )
        environment_variables = @{
            AZURE_AI_MODEL_DEPLOYMENT_NAME = $ModelDeployment
            TOOLBOX_NAME = $ToolboxName
        }
}

if (-not [string]::IsNullOrWhiteSpace($GitHubConnectionName)) {
    $Definition.environment_variables.GITHUB_TOKEN = '$' + "{{connections.$GitHubConnectionName.credentials.github_token}}"
}

if (-not [string]::IsNullOrWhiteSpace($AzureDevOpsConnectionName)) {
    $Definition.environment_variables.AZURE_DEVOPS_TOKEN = '$' + "{{connections.$AzureDevOpsConnectionName.credentials.ado_token}}"
    $Definition.environment_variables.AZURE_DEVOPS_AUTH_SCHEME = $AzureDevOpsAuthScheme
}

$AgentStatusCode = 0
$AgentLookup = Invoke-RestMethod `
    -Method Get `
    -Uri "$ProjectEndpoint/agents/$AgentName`?api-version=v1" `
    -Headers $Headers `
    -SkipHttpErrorCheck `
    -StatusCodeVariable AgentStatusCode

if ($AgentStatusCode -eq 200) {
    $AgentExists = $true
}
elseif ($AgentStatusCode -eq 404) {
    $AgentExists = $false
}
else {
    $LookupError = $AgentLookup | ConvertTo-Json -Depth 10 -Compress
    throw "Failed to look up agent '$AgentName'. HTTP $AgentStatusCode. Response: $LookupError"
}

if ($AgentExists) {
    $DeployUri = "$ProjectEndpoint/agents/$AgentName/versions?api-version=v1"
    $Body = @{ definition = $Definition } | ConvertTo-Json -Depth 10
    Write-Host "Agent already exists; creating a new version."
}
else {
    $DeployUri = "$ProjectEndpoint/agents?api-version=v1"
    $Body = @{ name = $AgentName; definition = $Definition } | ConvertTo-Json -Depth 10
    Write-Host "Creating agent and its first version."
}

$Agent = Invoke-RestMethod `
  -Method Post `
    -Uri $DeployUri `
  -Headers $Headers `
  -Body $Body

$Agent | ConvertTo-Json -Depth 10

$Version = [string]$Agent.version
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "The deployment response did not contain an agent version."
}

for ($Attempt = 1; $Attempt -le 60; $Attempt++) {
    $VersionDetails = Invoke-RestMethod `
        -Method Get `
        -Uri "$ProjectEndpoint/agents/$AgentName/versions/${Version}?api-version=v1" `
        -Headers $Headers

    Write-Host "Version $Version status: $($VersionDetails.status) ($Attempt/60)"
    if ($VersionDetails.status -eq "active") {
        Write-Host "Deployment succeeded."
        break
    }

    if ($VersionDetails.status -eq "failed") {
        $ErrorMessage = $VersionDetails.error.message
        throw "Hosted Agent provisioning failed: $ErrorMessage"
    }

    if ($Attempt -eq 60) {
        throw "Timed out waiting for Hosted Agent version $Version to become active."
    }

    Start-Sleep -Seconds 5
}