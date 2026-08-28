using System.Globalization;

namespace Atalaya.App.Services;

/// <summary>
/// Una versión SemVer, lo justo para poder contestar «¿es esta más nueva que la mía?» (F8 §3).
/// <para>
/// No se usa <see cref="System.Version"/> porque no es lo mismo: <c>System.Version</c> no entiende
/// de pre-releases —<c>1.2.0-beta</c> ni siquiera parsea— y ordena por cuatro números sin más. En
/// SemVer un pre-release es <b>anterior</b> a su versión final (1.2.0-beta &lt; 1.2.0), y esa
/// regla es justo la que impide que un <c>v2.0.0-rc1</c> etiquetado por error le salte a todo el
/// equipo como «hay versión nueva».
/// </para>
/// <para>
/// Los metadatos de build (<c>+sha</c>) se descartan: SemVer dice explícitamente que no
/// participan en la comparación, y es lo que el compilador añade a
/// <c>AssemblyInformationalVersion</c> cuando hay SourceLink.
/// </para>
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>La parte tras el guion, sin él. Vacía en una versión final.</summary>
    public string Prerelease { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>«1.2.3» o «1.2.3-rc.1». Sin la «v» del tag y sin metadatos de build.</summary>
    public override string ToString()
        => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>«1.2» — lo que se enseña en el banner: el número que la gente dice en voz alta.</summary>
    public string Short => IsPrerelease ? ToString() : $"{Major}.{Minor}";

    /// <summary>
    /// Parsea una versión tolerando lo que de verdad llega: la <c>v</c> del tag de git, el cuarto
    /// número que .NET mete en <c>FileVersion</c> (<c>1.2.3.0</c>) y los metadatos de build.
    /// Devuelve null si no hay ni un número reconocible — y quien pregunta se calla, que es lo que
    /// tiene que hacer un chequeo de cortesía ante un dato que no entiende.
    /// </summary>
    public static SemanticVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string s = text!.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s[1..];
        }

        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string prerelease = string.Empty;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
        }

        string[] parts = s.Split('.');
        if (parts.Length == 0 || !TryNumber(parts[0], out int major))
        {
            return null;
        }

        // El cuarto número de .NET (1.2.3.0) se ignora: SemVer tiene tres.
        int minor = parts.Length > 1 && TryNumber(parts[1], out int m) ? m : 0;
        int patch = parts.Length > 2 && TryNumber(parts[2], out int p) ? p : 0;
        return new SemanticVersion(major, minor, patch, prerelease.Trim());
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int byNumber = Major.CompareTo(other.Major);
        if (byNumber != 0)
        {
            return byNumber;
        }

        byNumber = Minor.CompareTo(other.Minor);
        if (byNumber != 0)
        {
            return byNumber;
        }

        byNumber = Patch.CompareTo(other.Patch);
        if (byNumber != 0)
        {
            return byNumber;
        }

        // Misma terna: una final gana a cualquier pre-release suya (1.2.0 > 1.2.0-rc.1).
        if (!IsPrerelease && !other.IsPrerelease)
        {
            return 0;
        }

        if (!IsPrerelease)
        {
            return 1;
        }

        if (!other.IsPrerelease)
        {
            return -1;
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public bool IsNewerThan(SemanticVersion? other) => CompareTo(other) > 0;

    /// <summary>
    /// Los identificadores del pre-release, punto a punto: los numéricos por su valor, el resto
    /// por orden alfabético, y un numérico es menor que uno alfanumérico (SemVer §11.4).
    /// </summary>
    private static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length)
            {
                return -1;  // el que se queda sin identificadores es el menor
            }

            if (i >= b.Length)
            {
                return 1;
            }

            bool aNum = TryNumber(a[i], out int an);
            bool bNum = TryNumber(b[i], out int bn);

            int step = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };

            if (step != 0)
            {
                return step;
            }
        }

        return 0;
    }

    private static bool TryNumber(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
