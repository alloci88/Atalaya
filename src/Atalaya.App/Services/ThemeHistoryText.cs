using System.Globalization;
using Atalaya.Copilot;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Cómo se ESCRIBE el historial de temáticas de un ciclo (F17.1), en un solo sitio para que el
/// panel del ciclo, el informe de cierre y el tooltip de la cinta digan lo mismo.
/// </summary>
public static class ThemeHistoryText
{
    private static CultureInfo Culture => AppCulture.Display;

    /// <summary>«Rendimiento → Seguridad»: las lupas por las que pasó, sin repetir consecutivas.</summary>
    public static string Chain(IReadOnlyList<ThemePeriod> periods)
    {
        var names = new List<string>();
        foreach (ThemePeriod p in periods)
        {
            string name = ThemeCatalog.Display(p.Theme);
            if (names.Count == 0 || names[^1] != name)
            {
                names.Add(name);
            }
        }

        return string.Join(" → ", names);
    }

    /// <summary>Una línea por periodo: «Rendimiento · del 2 sep 15:02 al 2 sep 15:26» / «Seguridad · desde el 2 sep 15:26 · cambiada por X».</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<ThemePeriod> periods)
    {
        var lines = new List<string>(periods.Count);
        foreach (ThemePeriod p in periods)
        {
            string when = (p.FromUtc, p.ToUtc) switch
            {
                (null, null) => "desde el inicio del ciclo",
                ({ } f, null) => $"desde el {Stamp(f)}",
                (null, { } t) => $"hasta el {Stamp(t)}",
                ({ } f, { } t) => $"del {Stamp(f)} al {Stamp(t)}",
            };
            string by = string.IsNullOrWhiteSpace(p.By) ? string.Empty : $" · cambiada por {p.By}";
            lines.Add($"{ThemeCatalog.Display(p.Theme)} · {when}{by}");
        }

        return lines;
    }

    /// <summary>
    /// Lo que el panel dice DEBAJO del distintivo cuando el ciclo cambió de lupa: los periodos
    /// anteriores, del más reciente al más antiguo. Vacío si no cambió.
    /// </summary>
    public static string Previous(IReadOnlyList<ThemePeriod> periods)
    {
        if (periods.Count <= 1)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (int i = periods.Count - 2; i >= 0; i--)
        {
            ThemePeriod p = periods[i];
            string until = p.ToUtc is { } t ? $"hasta el {Stamp(t)}" : "hasta el cambio";
            string by = string.IsNullOrWhiteSpace(periods[i + 1].By) ? string.Empty : $", cambiada por {periods[i + 1].By}";
            parts.Add($"{ThemeCatalog.Display(p.Theme)} ({until}{by})");
        }

        return "Antes: " + string.Join("; ", parts);
    }

    private static string Stamp(DateTimeOffset d) => d.ToLocalTime().ToString("d MMM HH:mm", Culture);
}
