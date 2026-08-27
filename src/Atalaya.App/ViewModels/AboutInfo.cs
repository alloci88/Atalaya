using System.Diagnostics;
using System.Reflection;
using Atalaya.App.Services;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Lo que dice el «Acerca de» (F6.4 §3): quién es la aplicación, qué versión es ESTA y de qué
/// organización.
/// <para>
/// Es una clase aparte del diálogo por la razón de siempre: la versión y el nombre de la
/// organización son datos que se pueden equivocar, y un dato que solo existe dentro de una
/// ventana no se puede comprobar.
/// </para>
/// </summary>
public sealed class AboutInfo
{
    /// <summary>Dónde vive el código. Es el enlace del diálogo.</summary>
    public const string RepositoryUrl = "https://github.com/maxam/atalaya";

    /// <summary>El manual de usuario, dentro del propio repositorio.</summary>
    public const string ManualUrl = "https://github.com/maxam/atalaya/blob/main/MANUAL.md";

    public AboutInfo(string? organization, string version)
    {
        Organization = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
        Version = version;
    }

    /// <summary>La organización del hub, o null si el hub todavía no dice cuál es.</summary>
    public string? Organization { get; }

    /// <summary>«1.0.0». La del ensamblado que se está ejecutando, no una constante escrita aquí.</summary>
    public string Version { get; }

    public string VersionLabel => $"Versión {Version}";

    /// <summary>Sin organización no se escribe una línea vacía: el bloque no aparece.</summary>
    public bool HasOrganization => Organization is not null;

    /// <summary>«Atalaya · Maxam», o solo «Atalaya». Es la misma firma que va al pie del informe.</summary>
    public string Signature => HasOrganization ? $"Atalaya · {Organization}" : "Atalaya";

    /// <inheritdoc cref="RepositoryUrl"/>
    public string Repository => RepositoryUrl;

    /// <inheritdoc cref="ManualUrl"/>
    public string Manual => ManualUrl;

    /// <summary>Lo construye desde el hub y desde el ensamblado vivo.</summary>
    public static AboutInfo Create(HubContext hub)
        => new(hub.OrganizationName, CurrentVersion());

    /// <summary>
    /// La versión REAL del binario. Se prefiere la informativa —la que puede llevar sufijos como
    /// <c>+sha</c>— y se recorta al número: es la que un despliegue puede sellar sin que aquí haya
    /// que tocar nada.
    /// </summary>
    public static string CurrentVersion()
    {
        Assembly assembly = typeof(AboutInfo).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        string? file = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        return string.IsNullOrWhiteSpace(file) ? assembly.GetName().Version?.ToString() ?? "—" : file;
    }

}
