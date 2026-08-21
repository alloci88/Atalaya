using Wpf.Ui.Appearance;

namespace Atalaya.App.Services;

/// <summary>Applies the light/dark Fluent theme (§8).</summary>
public static class ThemeService
{
    public static void Apply(string theme)
        => ApplicationThemeManager.Apply(
            string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark);
}
