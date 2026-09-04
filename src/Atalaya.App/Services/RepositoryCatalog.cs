namespace Atalaya.App.Services;

/// <summary>
/// Los repositorios donde puede vivir una aplicación de este equipo (R3): los de la organización
/// de la cuenta conectada, pedidos con SU token y recordados mientras dure la sesión de la app.
/// <para>
/// Existe porque el alta dejó de ser un formulario en blanco. La organización ya está identificada
/// —es la del hub—, así que el repositorio de una app nueva no es un texto que se escribe sino uno
/// de una lista, y el nombre de la app es el del repositorio. Escribirlos a mano era la única
/// forma de equivocarse.
/// </para>
/// <para>
/// La llamada va por <see cref="GitHubApiClient"/>, que es quien ya habla con la API con el token
/// de la cuenta: una segunda pila HTTP aquí sería un segundo sitio donde acordarse de mandar el
/// token, de traducir un 403 de SAML y de marcar la cuenta cuando GitHub la rechaza.
/// </para>
/// </summary>
public sealed class RepositoryCatalog
{
    private readonly GitHubAccountService _account;
    private readonly GitHubApiClient _api;
    private readonly DeployConfig _deploy;
    private readonly HubContext _hub;

    /// <summary>
    /// Lo último que GitHub contestó. Se cachea EN LA SESIÓN y no en disco: una lista de repos
    /// caduca cuando alguien crea uno, y un fichero con la lista de ayer sería una lista en la que
    /// el repo nuevo no sale y nadie sabe por qué. El botón de recargar es la salida.
    /// </summary>
    private IReadOnlyList<GitHubRepository>? _cache;

    public RepositoryCatalog(
        GitHubAccountService account, GitHubApiClient api, DeployConfig deploy, HubContext hub)
    {
        _account = account;
        _api = api;
        _deploy = deploy;
        _hub = hub;
    }

    /// <summary>
    /// De quién son los repositorios: la organización que el despliegue nombra y, si no nombra
    /// ninguna, el dueño del repositorio del hub. Es la misma organización donde vive Atalaya, y
    /// sale de donde ya estaba escrita — no de un ajuste nuevo que alguien tendría que rellenar.
    /// </summary>
    public string? Owner => _deploy.ChecksOrgMembership
        ? _deploy.OrganizationLogin.Trim()
        : GitHubApiClient.ParseRepositoryUrl(_hub.HubUrl)?.Owner;

    /// <summary>True cuando la lista ya está en memoria y pedirla no cuesta una llamada.</summary>
    public bool IsCached => _cache is not null;

    /// <summary>
    /// La lista. Con <paramref name="refresh"/> vuelve a preguntar a GitHub aunque haya caché —es
    /// lo que hace el botón de recargar, y sin ello un repo recién creado no aparecería hasta
    /// reiniciar la aplicación—.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepository>> ListAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _cache is { } cached)
        {
            return cached;
        }

        if (_account.Token is not { Length: > 0 } token)
        {
            throw new InvalidOperationException(
                "No hay cuenta de GitHub conectada: conéctala en Cuenta para ver los repositorios.");
        }

        if (Owner is not { Length: > 0 } owner)
        {
            throw new InvalidOperationException(
                "Este despliegue no dice de qué organización son los repositorios.");
        }

        try
        {
            _cache = await _api.ListOwnerRepositoriesAsync(token, owner, ct);
            return _cache;
        }
        catch (Exception ex)
        {
            // Un 401 aquí es el mismo 401 de todo lo demás: la cuenta pasa a ámbar y el usuario
            // acaba en Cuenta, en vez de ver solo un desplegable vacío en esta pantalla.
            _account.NoteFailure(ex);
            throw;
        }
    }
}
