using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Atalaya.App.Services;

/// <summary>The bits of <c>GET /user</c> Atalaya needs (D2.2).</summary>
public sealed record GitHubUser(long Id, string Login, string? Name, string? AvatarUrl, string? Email)
{
    /// <summary>
    /// Display name, falling back to the login.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Login : Name!;

    /// <summary>
    /// The email to use for hub commits: the account's public email when it has one, else the
    /// GitHub <c>noreply</c> address, which is always valid and never leaks a private address.
    /// </summary>
    public string CommitEmail => string.IsNullOrWhiteSpace(Email)
        ? $"{Id}+{Login}@users.noreply.github.com"
        : Email!;
}

/// <summary>What went wrong talking to the GitHub API, mapped to an actionable remedy (D2.3).</summary>
public enum GitHubApiProblem
{
    None = 0,

    /// <summary>401: the token was revoked or expired — reconnect.</summary>
    TokenRejected,

    /// <summary>403 + <c>X-GitHub-SSO</c>: the org requires a SAML session for this token.</summary>
    SamlRequired,

    /// <summary>403 without SSO: the org's OAuth App policy has not approved Atalaya.</summary>
    OrgPolicyBlocked,

    /// <summary>Could not reach github.com.</summary>
    Offline,

    Unknown,
}

/// <summary>An API call that failed, carrying the specific remedy text.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(GitHubApiProblem problem, string message, Exception? inner = null)
        : base(message, inner) => Problem = problem;

    public GitHubApiProblem Problem { get; }
}

/// <summary>
/// The slice of the GitHub REST API the connection flow needs: the signed-in user's profile and
/// organization membership. Nothing else — Atalaya talks to the hub over git, not over the API.
/// </summary>
public sealed class GitHubApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public GitHubApiClient(HttpClient? http = null, string? baseUrl = null)
    {
        _http = http ?? new HttpClient();
        _baseUrl = (baseUrl ?? "https://api.github.com").TrimEnd('/');
    }

    /// <summary>Profile of the token's owner: login, name, avatar and email (D2.2).</summary>
    public async Task<GitHubUser> GetCurrentUserAsync(string token, CancellationToken ct)
    {
        using JsonDocument doc = await GetJsonAsync("/user", token, ct);
        JsonElement root = doc.RootElement;

        string? login = ReadString(root, "login");
        if (login is null)
        {
            throw new GitHubApiException(GitHubApiProblem.Unknown,
                "GitHub no devolvió el perfil de la cuenta. Reintenta en unos segundos.");
        }

        return new GitHubUser(
            ReadLong(root, "id") ?? 0,
            login,
            ReadString(root, "name"),
            ReadString(root, "avatar_url"),
            ReadString(root, "email"));
    }

    /// <summary>
    /// Whether the token's owner belongs to <paramref name="org"/>. Uses <c>GET /user/orgs</c>,
    /// which needs only <c>read:org</c> and lists both public and private memberships.
    /// </summary>
    public async Task<bool> IsOrganizationMemberAsync(string token, string org, CancellationToken ct)
    {
        using JsonDocument doc = await GetJsonAsync("/user/orgs?per_page=100", token, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            if (string.Equals(ReadString(item, "login"), org, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Atalaya");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException(GitHubApiProblem.Offline,
                "No hay conexión con github.com. Comprueba la red o el proxy y reintenta.", ex);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw Translate(response, body);
            }

            try
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }
            catch (JsonException ex)
            {
                throw new GitHubApiException(GitHubApiProblem.Unknown,
                    $"Respuesta inesperada de GitHub ({(int)response.StatusCode}).", ex);
            }
        }
    }

    /// <summary>Maps an HTTP failure onto the specific remedy the user can act on (D2.3).</summary>
    internal static GitHubApiException Translate(HttpResponseMessage response, string body)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new GitHubApiException(GitHubApiProblem.TokenRejected, ConnectionHelp.TokenRejected);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            bool saml = response.Headers.TryGetValues("X-GitHub-SSO", out _)
                        || body.Contains("saml", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("single sign-on", StringComparison.OrdinalIgnoreCase);

            return saml
                ? new GitHubApiException(GitHubApiProblem.SamlRequired, ConnectionHelp.SamlRequired)
                : new GitHubApiException(GitHubApiProblem.OrgPolicyBlocked, ConnectionHelp.OrgPolicyBlocked);
        }

        return new GitHubApiException(GitHubApiProblem.Unknown,
            $"GitHub respondió {(int)response.StatusCode}. Reintenta en unos segundos.");
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out long n)
            ? n
            : null;
}
