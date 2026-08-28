using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atalaya.App.Services;

/// <summary>
/// Deployment configuration (D1): the things an administrator sets once for the whole team and
/// that a user must never see or type — the hub URL, the OAuth App client id, and the
/// organization to check membership against.
/// <para>
/// Resolution order: <c>appsettings.deploy.json</c> next to the executable (what a corporate
/// deployment edits), else the copy embedded in the assembly (the default). Migrating the hub to
/// the organization's repo is a one-file change in the deployment — zero user actions.
/// </para>
/// </summary>
public sealed class DeployConfig
{
    public const string FileName = "appsettings.deploy.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The audit-hub repository. Opaque to the user (never shown in normal UI).</summary>
    public string HubUrl { get; set; } = string.Empty;

    /// <summary>
    /// Client id of the "Atalaya" GitHub OAuth App with device flow enabled. NOT a secret —
    /// device flow needs no client secret, so embedding it here is by design (see README annex).
    /// </summary>
    public string GitHubClientId { get; set; } = string.Empty;

    /// <summary>
    /// Organization login to verify membership against after login. Empty = skip the check
    /// (correct while the hub is still a personal repo: the clone itself is the real gate).
    /// </summary>
    public string OrganizationLogin { get; set; } = string.Empty;

    /// <summary>
    /// El repositorio de la PROPIA Atalaya, de donde salen sus Releases (F8 §3). Es lo que el
    /// chequeo de versión consulta al arrancar.
    /// <para>
    /// Va aquí y no junto al hub porque son dos repositorios distintos con dos vidas distintas: el
    /// hub guarda las auditorías del equipo y puede migrar de sitio, mientras que este es el del
    /// código de la herramienta. Acoplarlos habría hecho que mover el hub apagara el aviso de
    /// versión, que es de las cosas que nadie descubre hasta que lleva meses sin actualizarse.
    /// </para>
    /// <para>
    /// Vacío = no se comprueba nada y no se avisa de nada, en silencio. Es el estado correcto de un
    /// despliegue que todavía no publica Releases.
    /// </para>
    /// </summary>
    public string AppRepoUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasClientId => !string.IsNullOrWhiteSpace(GitHubClientId);

    /// <summary>¿Hay un repositorio de la aplicación al que preguntar por versiones nuevas?</summary>
    [JsonIgnore]
    public bool ChecksForUpdates => !string.IsNullOrWhiteSpace(AppRepoUrl);

    [JsonIgnore]
    public bool ChecksOrgMembership => !string.IsNullOrWhiteSpace(OrganizationLogin);

    [JsonIgnore]
    public bool HasHubUrl => !string.IsNullOrWhiteSpace(HubUrl);

    /// <summary>Where the on-disk override is expected. Shown in diagnostics.</summary>
    public static string OverridePath(string? baseDirectory = null)
        => Path.Combine(baseDirectory ?? AppContext.BaseDirectory, FileName);

    public static DeployConfig Load(string? baseDirectory = null)
    {
        string onDisk = OverridePath(baseDirectory);
        if (File.Exists(onDisk))
        {
            try
            {
                return Parse(File.ReadAllText(onDisk));
            }
            catch
            {
                // A corrupt override must not brick the app; fall through to the embedded default.
            }
        }

        return LoadEmbedded();
    }

    internal static DeployConfig LoadEmbedded()
    {
        Assembly asm = typeof(DeployConfig).Assembly;
        string? name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(FileName, StringComparison.Ordinal));
        if (name is null)
        {
            return new DeployConfig();
        }

        using Stream? stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return new DeployConfig();
        }

        using var reader = new StreamReader(stream);
        try
        {
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return new DeployConfig();
        }
    }

    internal static DeployConfig Parse(string json)
        => JsonSerializer.Deserialize<DeployConfig>(json, JsonOptions) ?? new DeployConfig();
}
