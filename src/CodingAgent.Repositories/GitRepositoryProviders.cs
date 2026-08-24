using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodingAgent.Core.Execution;
using CodingAgent.Core.Repositories;

namespace CodingAgent.Repositories;

public abstract class GitRepositoryProvider(IProcessRunner processRunner, HttpClient? httpClient = null) : IRepositoryProvider
{
    protected IProcessRunner ProcessRunner { get; } = processRunner;
    protected HttpClient HttpClient { get; } = httpClient ?? new HttpClient();

    public abstract RepositoryProviderKind Kind { get; }

    public abstract bool TryParse(Uri repositoryUri, string revision, out RepositoryReference? reference);

    public async Task CloneAsync(RepositoryReference reference, string destination, CancellationToken cancellationToken)
    {
        if (reference.Provider != Kind)
        {
            throw new ArgumentException($"Provider {Kind} cannot clone {reference.Provider} repositories.", nameof(reference));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var result = await ProcessRunner.RunAsync(
            new ProcessRequest(
                "git",
                ["clone", "--depth", "1", "--branch", reference.Revision, "--", reference.CloneUri.AbsoluteUri, destination],
                Path.GetDirectoryName(destination)!,
                TimeSpan.FromMinutes(5),
                CreateGitAuthenticationEnvironment(required: false)),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Repository clone failed: {result.StandardError}");
        }
    }

    public abstract Task<PullRequestResult> PublishPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken);

    protected abstract string? GetAuthorizationHeader(bool required);

    protected IReadOnlyDictionary<string, string>? CreateGitAuthenticationEnvironment(bool required)
    {
        var authorization = GetAuthorizationHeader(required);
        return authorization is null
            ? null
            : new Dictionary<string, string>
            {
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
                ["GIT_CONFIG_VALUE_0"] = $"Authorization: {authorization}",
                ["GIT_TERMINAL_PROMPT"] = "0",
            };
    }

    protected async Task PushAgentBranchAsync(PullRequestRequest request, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            new ProcessRequest(
                "git",
                ["push", "origin", "--set-upstream", $"HEAD:refs/heads/{request.SourceBranch}"],
                request.Workspace,
                TimeSpan.FromMinutes(5),
                CreateGitAuthenticationEnvironment(required: true)),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Branch push failed: {result.StandardError}");
        }
    }

    protected static async Task<string> GetErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return content.Length <= 2000 ? content : content[..2000];
    }
}

public sealed class GitHubRepositoryProvider(IProcessRunner processRunner, HttpClient? httpClient = null)
    : GitRepositoryProvider(processRunner, httpClient)
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
        reference = new RepositoryReference(Kind, repositoryUri, repositoryName, revision, segments[0]);
        return true;
    }

    public override async Task<PullRequestResult> PublishPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken)
    {
        await PushAgentBranchAsync(request, cancellationToken);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{Uri.EscapeDataString(request.Repository.Organization!)}/{Uri.EscapeDataString(request.Repository.RepositoryName)}/pulls");
        message.Headers.Authorization = AuthenticationHeaderValue.Parse(GetAuthorizationHeader(required: true)!);
        message.Headers.UserAgent.ParseAdd("Microsoft-Foundry-Coding-Agent/1.0");
        message.Headers.Accept.ParseAdd("application/vnd.github+json");
        message.Content = JsonContent.Create(new
        {
            title = request.Title,
            head = request.SourceBranch,
            @base = request.TargetBranch,
            body = request.Description,
        });

        using var response = await HttpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub pull request creation failed ({(int)response.StatusCode}): {await GetErrorAsync(response, cancellationToken)}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return new PullRequestResult(
            new Uri(document.RootElement.GetProperty("html_url").GetString()!),
            document.RootElement.GetProperty("number").GetInt32().ToString());
    }

    protected override string? GetAuthorizationHeader(bool required)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (required && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GITHUB_TOKEN is not configured for this Agent version.");
        }

        return string.IsNullOrWhiteSpace(token) ? null : $"Bearer {token}";
    }
}

public sealed class AzureDevOpsRepositoryProvider(IProcessRunner processRunner, HttpClient? httpClient = null)
    : GitRepositoryProvider(processRunner, httpClient)
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

        var organization = isModern ? segments[0] : repositoryUri.Host[..^".visualstudio.com".Length];
        reference = new RepositoryReference(Kind, repositoryUri, segments[gitIndex + 1], revision, organization, segments[gitIndex - 1]);
        return true;
    }

    public override async Task<PullRequestResult> PublishPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken)
    {
        await PushAgentBranchAsync(request, cancellationToken);

        var organization = Uri.EscapeDataString(request.Repository.Organization!);
        var project = Uri.EscapeDataString(request.Repository.Project!);
        var repository = Uri.EscapeDataString(request.Repository.RepositoryName);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repository}/pullrequests?api-version=7.1");
        message.Headers.Authorization = AuthenticationHeaderValue.Parse(GetAuthorizationHeader(required: true)!);
        message.Content = JsonContent.Create(new
        {
            sourceRefName = $"refs/heads/{request.SourceBranch}",
            targetRefName = $"refs/heads/{request.TargetBranch}",
            title = request.Title,
            description = request.Description,
        });

        using var response = await HttpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure DevOps pull request creation failed ({(int)response.StatusCode}): {await GetErrorAsync(response, cancellationToken)}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var id = document.RootElement.GetProperty("pullRequestId").GetInt32().ToString();
        var url = new Uri($"https://dev.azure.com/{organization}/{project}/_git/{repository}/pullrequest/{id}");
        return new PullRequestResult(url, id);
    }

    protected override string? GetAuthorizationHeader(bool required)
    {
        var token = Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN");
        if (required && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("AZURE_DEVOPS_TOKEN is not configured for this Agent version.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var scheme = Environment.GetEnvironmentVariable("AZURE_DEVOPS_AUTH_SCHEME") ?? "Basic";
        return scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? $"Bearer {token}"
            : $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($":{token}"))}";
    }
}