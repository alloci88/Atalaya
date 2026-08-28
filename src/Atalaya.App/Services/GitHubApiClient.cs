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

/// <summary>
/// Una Release publicada del repositorio de la propia aplicación (F8 §3): su tag, su página y su
/// título. No se pide nada más — el aviso solo necesita saber QUÉ versión hay y ADÓNDE llevar al
/// usuario; la descarga la hace él, en el navegador.
/// </summary>
/// <param name="TagName">El tag tal cual (<c>v1.2.3</c>). Es la única fuente de la versión.</param>
/// <param name="HtmlUrl">La página de la Release. Null si GitHub no la devolvió.</param>
public sealed record GitHubRelease(string TagName, string? HtmlUrl, string? Name);

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

    /// <summary>
    /// 404. En este API significa las dos cosas a la vez —no existe, o existe y no lo puedes ver—,
    /// así que quien pregunta decide qué quiere decir en su caso. Para el chequeo de versión es
    /// «este repositorio aún no ha publicado ninguna Release», que no es un error (F8 §3).
    /// </summary>
    NotFound,

    Unknown,
}

/// <summary>What the connected account can do with the hub repository.</summary>
public enum RepositoryAccess
{
    /// <summary>Read and write: the hub works.</summary>
    ReadWrite,

    /// <summary>Can clone and pull, but a push will fail with a misleading 404.</summary>
    ReadOnly,

    /// <summary>Does not exist, or exists and this account may not see it.</summary>
    NotVisible,

    /// <summary>The organization has not approved the OAuth App for its repositories.</summary>
    OrgPolicyBlocked,

    /// <summary>The organization enforces SAML and this token has no active SSO session.</summary>
    SamlRequired,

    /// <summary>The token was rejected outright.</summary>
    TokenRejected,

    /// <summary>Could not find out (offline, or not a GitHub URL).</summary>
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

    /// <summary>
    /// What the token can actually do with a repository. GitHub answers <b>404</b> both for
    /// "does not exist" and for "exists but you may not see it" — and a push without write access
    /// is a 404 too — so git alone can never tell those apart. This asks the API, which can.
    /// </summary>
    public async Task<RepositoryAccess> GetRepositoryAccessAsync(string token, string owner, string repo, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = await GetJsonAsync($"/repos/{owner}/{repo}", token, ct);
            JsonElement root = doc.RootElement;

            bool canPush = root.ValueKind == JsonValueKind.Object
                           && root.TryGetProperty("permissions", out JsonElement perms)
                           && perms.ValueKind == JsonValueKind.Object
                           && perms.TryGetProperty("push", out JsonElement push)
                           && push.ValueKind == JsonValueKind.True;

            return canPush ? RepositoryAccess.ReadWrite : RepositoryAccess.ReadOnly;
        }
        catch (GitHubApiException ex)
        {
            return ex.Problem switch
            {
                GitHubApiProblem.SamlRequired => RepositoryAccess.SamlRequired,
                GitHubApiProblem.OrgPolicyBlocked => RepositoryAccess.OrgPolicyBlocked,
                GitHubApiProblem.TokenRejected => RepositoryAccess.TokenRejected,
                GitHubApiProblem.Offline => RepositoryAccess.Unknown,
                _ => RepositoryAccess.NotVisible,
            };
        }
    }

    /// <summary>
    /// La última Release PUBLICADA del repositorio (F8 §3), o null si no hay ninguna.
    /// <para>
    /// Usa <c>GET /repos/{owner}/{repo}/releases/latest</c>, que ya excluye borradores y
    /// pre-releases: el aviso de versión nueva no puede dispararse con un <c>v2.0.0-rc1</c> que
    /// alguien subió para probar. El repositorio es privado y el token de la cuenta conectada ya
    /// tiene acceso — cero credenciales nuevas.
    /// </para>
    /// <para>
    /// Un 404 aquí es normal y no es un error: significa «este repositorio todavía no ha publicado
    /// ninguna versión». Se devuelve null y quien pregunta se calla.
    /// </para>
    /// </summary>
    public async Task<GitHubRelease?> GetLatestReleaseAsync(
        string token, string owner, string repo, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync($"/repos/{owner}/{repo}/releases/latest", token, ct);
        }
        catch (GitHubApiException ex) when (ex.Problem == GitHubApiProblem.NotFound)
        {
            return null;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            string? tag = ReadString(root, "tag_name");
            return string.IsNullOrWhiteSpace(tag)
                ? null
                : new GitHubRelease(tag!, ReadString(root, "html_url"), ReadString(root, "name"));
        }
    }

    /// <summary>
    /// Splits <c>https://github.com/owner/repo(.git)</c> into its two parts, or null when the URL
    /// is not a GitHub repository we can ask about (a local path, another host…).
    /// </summary>
    public static (string Owner, string Repo)? ParseRepositoryUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return string.IsNullOrEmpty(repo) ? null : (parts[0], repo);
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

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GitHubApiException(GitHubApiProblem.NotFound,
                "GitHub respondió 404: el recurso no existe, o esta cuenta no puede verlo.");
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
