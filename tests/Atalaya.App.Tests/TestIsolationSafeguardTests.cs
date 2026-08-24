using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F3.1 Bloque 2: blindaje del arnés de tests. Si algún test se olvidara de aislar el
/// <see cref="AppPaths"/> y apuntara al almacén real (<c>%LOCALAPPDATA%\Atalaya</c>), el harness
/// debe cortarle el paso ANTES de escribir. Este test prueba la salvaguarda misma.
/// </summary>
public sealed class TestIsolationSafeguardTests
{
    [Fact]
    public void AssertIsolated_throws_when_root_points_to_real_LOCALAPPDATA_Atalaya()
    {
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atalaya");
        var paths = new AppPaths(real);

        Action act = () => TestFactory.AssertIsolated(paths);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*isolation violation*");
    }

    [Fact]
    public void AssertIsolated_accepts_temp_directory()
    {
        string temp = Path.Combine(Path.GetTempPath(), "atalaya-iso", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(temp);
            Action act = () => TestFactory.AssertIsolated(paths);
            act.Should().NotThrow();
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
