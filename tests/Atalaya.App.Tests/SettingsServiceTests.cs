using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public SettingsServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-settings", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
    }

    [Fact]
    public void Pat_roundtrips_through_dpapi()
    {
        var service = new SettingsService(_paths);
        service.Load();
        service.SetPat("ghp_secret_token_123");

        service.GetPat().Should().Be("ghp_secret_token_123");

        // Persisted PAT is not the plaintext.
        File.ReadAllText(_paths.SettingsJson).Should().NotContain("ghp_secret_token_123");
    }

    [Fact]
    public void Settings_persist_across_reload()
    {
        var service = new SettingsService(_paths);
        var s = service.Load();
        s.HubRepoUrl = "https://example/hub.git";
        s.Theme = "light";
        service.Save(s);

        var reloaded = new SettingsService(_paths).Load();
        reloaded.HubRepoUrl.Should().Be("https://example/hub.git");
        reloaded.Theme.Should().Be("light");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
