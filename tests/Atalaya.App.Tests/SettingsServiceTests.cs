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

    // ============================================================ D-563: la clave heredada

    /// <summary>
    /// Un <c>settings.json</c> que NO trae la clave estrena el arreglo asistido encendido. Este
    /// era el caso que D-559 daba por único, y es el que siempre funcionó: el deserializador
    /// respeta el inicializador de la propiedad cuando la clave falta.
    /// </summary>
    [Fact]
    public void Un_settings_sin_la_clave_estrena_el_arreglo_asistido_encendido()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsJson, """{"theme":"dark","pollingSeconds":60}""");

        new SettingsService(_paths).Load().EnableAssistedFix.Should().BeTrue();
    }

    /// <summary>
    /// <b>El bug de verdad.</b> Antes de F6.9 el flag existía con valor por defecto <c>false</c> y
    /// <c>Save</c> lo escribía igual que a todos los demás. En esas máquinas la clave NO falta:
    /// vale <c>false</c> con todas las letras, y cambiar el defecto de la propiedad no las alcanza.
    /// El botón «Arreglar con agente» no aparecía en ninguna de ellas.
    /// </summary>
    [Fact]
    public void Un_false_heredado_de_antes_de_F6_9_se_promociona_una_vez()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsJson, """{"theme":"dark","enableAssistedFix":false}""");

        var service = new SettingsService(_paths);
        service.Load().EnableAssistedFix.Should().BeFalse("así estaba escrito en disco");

        service.MigrateAssistedFixDefault();

        service.Current.EnableAssistedFix.Should().BeTrue();
        new SettingsService(_paths).Load().EnableAssistedFix
            .Should().BeTrue("la promoción se persiste, no vive solo en memoria");
    }

    /// <summary>
    /// Y después de promocionarlo una vez, apagarlo es una decisión del usuario: no se le vuelve
    /// a encender nunca. Una migración que corre en cada arranque no es una migración, es un
    /// ajuste que no se deja cambiar.
    /// </summary>
    [Fact]
    public void Una_vez_promocionado_apagarlo_a_mano_sobrevive_al_reinicio()
    {
        var first = new SettingsService(_paths);
        first.Load();
        first.MigrateAssistedFixDefault();

        AppSettings s = first.Current;
        s.EnableAssistedFix = false;
        first.Save(s);

        var next = new SettingsService(_paths);
        next.Load();
        next.MigrateAssistedFixDefault();

        next.Current.EnableAssistedFix.Should().BeFalse();
    }

    /// <summary>
    /// El otro miembro de la familia, comprobado y sano: <c>copilotModel</c> también cambió de
    /// valor por defecto ("gpt-5" en F5.1 → vacío en F5.15) y también quedó escrito en las
    /// máquinas de entonces. Ese NO necesita migración porque <c>ModelResolver</c> ya lo cura en
    /// caliente: un modelo que la cuenta no ofrece se sustituye y se guarda. Lo que este test fija
    /// es que el modelo guardado se lee tal cual y no se pisa al cargar.
    /// </summary>
    [Fact]
    public void El_modelo_guardado_se_respeta_al_cargar()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsJson, """{"copilotModel":"gpt-5"}""");

        new SettingsService(_paths).Load().CopilotModel.Should().Be("gpt-5");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
