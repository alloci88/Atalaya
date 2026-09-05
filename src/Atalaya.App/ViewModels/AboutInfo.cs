using System.Diagnostics;
using System.Reflection;
using Atalaya.App.Services;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Lo que dice el «Acerca de» (F6.4 §3): quién es la aplicación, qué versión es ESTA, de dónde
/// sale ese binario y de qué organización.
/// <para>
/// Es una clase aparte del diálogo por la razón de siempre: la versión y los enlaces son datos que
/// se pueden equivocar, y un dato que solo existe dentro de una ventana no se puede comprobar.
/// </para>
/// </summary>
public sealed class AboutInfo
{
    /// <summary>
    /// El sufijo que marca un build local (BUGFIX-VERSION). Lo estampa
    /// <c>Directory.Build.targets</c> cuando la versión NO viene del workflow de release.
    /// </summary>
    public const string DevelopmentSuffix = "-dev";

    /// <summary>Dónde vive el manual dentro del repositorio, desde su raíz.</summary>
    public const string ManualPath = "blob/main/MANUAL.md";

    public AboutInfo(string? organization, string version, string? repositoryUrl)
    {
        Organization = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
        Version = version;
        Repository = Normalize(repositoryUrl);
    }

    /// <summary>La organización del hub, o null si el hub todavía no dice cuál es.</summary>
    public string? Organization { get; }

    /// <summary>
    /// La versión informativa CRUDA del ensamblado: «1.0.3» en una release, «1.0.3-dev+0f920d9»
    /// en un build local.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// El repositorio, tal y como lo declara el despliegue. Null cuando no está configurado — y
    /// entonces no hay enlace, que es mejor que un enlace roto.
    /// </summary>
    public string? Repository { get; }

    /// <summary>
    /// El manual, DERIVADO del repositorio (BUGFIX-VERSION). No se escribe a mano en ninguna
    /// parte: dos URLs escritas por separado es como una de las dos acabó apuntando a un 404.
    /// </summary>
    public string? Manual => Repository is null ? null : $"{Repository}/{ManualPath}";

    public bool HasRepository => Repository is not null;

    public bool HasManual => Manual is not null;

    /// <summary>
    /// Qué se dice cuando no hay enlaces. Un hueco sin explicación se lee como un fallo de la
    /// aplicación; esto dice que falta un ajuste y cuál (N-2).
    /// </summary>
    public string NoLinksNotice =>
        $"Este despliegue no declara «appRepoUrl» en {DeployConfig.FileName}, así que no hay "
        + "adónde enlazar. Pídeselo a quien preparó la instalación.";

    public bool HasNoLinks => !HasRepository;

    /// <summary>Sin organización no se escribe una línea vacía: el bloque no aparece.</summary>
    public bool HasOrganization => Organization is not null;

    /// <summary>«Atalaya · Maxam», o solo «Atalaya». Es la misma firma que va al pie del informe.</summary>
    public string Signature => HasOrganization ? $"Atalaya · {Organization}" : "Atalaya";

    /// <summary>Este binario NO viene del workflow de release.</summary>
    public bool IsDevelopmentBuild => LooksLikeDevelopment(Version);

    /// <summary>Lo construye desde el hub, el despliegue y el ensamblado vivo.</summary>
    public static AboutInfo Create(HubContext hub, DeployConfig? deploy = null)
        => new(hub.OrganizationName, CurrentVersion(), deploy?.AppRepoUrl);

    /// <summary>
    /// La versión REAL del binario, cruda.
    /// <para>
    /// Se lee de <see cref="AssemblyInformationalVersionAttribute"/> y NO de
    /// <c>Assembly.GetName().Version</c>: la segunda es numérica de cuatro campos y se queda en
    /// <c>1.0.0.0</c> con muchísima facilidad, mientras que la informativa es la que lleva la
    /// SemVer completa con sus sufijos — que es justo donde vive la señal de «esto es un build
    /// local».
    /// </para>
    /// </summary>
    public static string CurrentVersion()
    {
        Assembly assembly = typeof(AboutInfo).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational!.Trim();
        }

        string? file = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        return string.IsNullOrWhiteSpace(file) ? assembly.GetName().Version?.ToString() ?? "—" : file;
    }

    /// <summary>
    /// El número SIN la marca de desarrollo ni los metadatos: «1.0.3» tanto en la release como en
    /// un «1.0.3-dev+0f920d9».
    /// <para>
    /// Es con lo que compara el chequeo de versión (BUGFIX-VERSION): un build local de 1.0.3-dev va
    /// por delante de la release 1.0.3, no por detrás, así que anunciarle que «existe la 1.0.3»
    /// sería mandarle a descargar lo que ya tiene. Pero cuando salga la 1.0.4, sí debe avisar.
    /// </para>
    /// </summary>
    public static string BaseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        string text = version!.Trim();
        int plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        int dash = text.IndexOf('-');
        return dash >= 0 ? text[..dash] : text;
    }

        /// <summary>
    /// Un pre-release marcado como desarrollo. Se mira el identificador COMPLETO tras el guion y
    /// no un «contiene»: un futuro <c>1.1.0-rc.1</c> es un pre-release de verdad, publicado por el
    /// workflow, y no puede leerse como un build de alguien en su portátil.
    /// </summary>
    private static bool LooksLikeDevelopment(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string text = version!.Trim();
        int plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        int dash = text.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        string prerelease = text[(dash + 1)..];
        return prerelease.Equals("dev", StringComparison.OrdinalIgnoreCase)
            || prerelease.StartsWith("dev.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>La URL del despliegue, sin barra final y solo si de verdad hay algo.</summary>
    private static string? Normalize(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : url!.Trim().TrimEnd('/');
}
