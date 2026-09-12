using Atalaya.Agents;
using Atalaya.App.Services;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
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
    /// <b>PROV-2 §2 — la adopción de los modelos guardados con el nombre viejo.</b> Un
    /// <c>settings.json</c> de hoy trae el modelo de cada casa en su campo propio
    /// —<c>copilotModel</c>, <c>claudeCodeModel</c>—; al pasar a un mapa por identificador, los
    /// dos acaban en el mapa, cada uno en la entrada de su casa.
    /// <para>
    /// <b>Lo que se rompería en silencio sin esto</b>: una actualización le borra a todo el mundo
    /// el modelo que tenía elegido, y no lo descubre hasta la siguiente auditoría — que es la
    /// peor forma posible de enterarse, con el barrido ya lanzado. Cada casa declara con qué
    /// nombre guardaba el suyo (<c>LegacyModelSettingKey</c>), así que la aplicación no tiene que
    /// saberse la tabla.
    /// </para>
    /// </summary>
    [Fact]
    public void Los_modelos_guardados_con_el_nombre_viejo_se_adoptan_en_el_mapa()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            _paths.SettingsJson,
            """{"copilotModel":"gpt-5","claudeCodeModel":"opus"}""");

        var settings = new SettingsService(_paths);
        settings.Load();
        settings.AdoptLegacyProviderModels(Houses()).Should().BeTrue();

        settings.ModelFor("copilot").Should().Be("gpt-5");
        settings.ModelFor("claude-code").Should().Be("opus");

        // Y sobrevive al reinicio: la adopción ESCRIBE, no se recalcula en cada arranque.
        var next = new SettingsService(_paths);
        next.Load();
        next.ModelFor("copilot").Should().Be("gpt-5");
        next.ModelFor("claude-code").Should().Be("opus");
    }

    /// <summary>
    /// Y el que estuviera vacío sigue vacío: adoptar no es inventar. Un campo viejo sin valor no
    /// crea entrada, y vacío sigue queriendo decir «pregúntaselo al runtime» (F5.15).
    /// </summary>
    [Fact]
    public void Una_casa_sin_modelo_guardado_sigue_sin_modelo()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            _paths.SettingsJson,
            """{"copilotModel":"gpt-5","claudeCodeModel":""}""");

        var settings = new SettingsService(_paths);
        settings.Load();
        settings.AdoptLegacyProviderModels(Houses());

        settings.ModelFor("copilot").Should().Be("gpt-5");
        settings.ModelFor("claude-code").Should().BeEmpty();
        settings.Current.ProviderModels.Should().ContainSingle();
    }

    /// <summary>
    /// Y no pisa lo que el usuario ya eligió: una adopción que corriera en cada arranque no sería
    /// una adopción, sería un ajuste que no se deja cambiar (la lección de la promoción de F6.9).
    /// </summary>
    [Fact]
    public void La_adopcion_no_pisa_el_modelo_que_ya_esta_en_el_mapa()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            _paths.SettingsJson,
            """{"copilotModel":"gpt-5","providerModels":{"copilot":"gpt-6-elegido-a-mano"}}""");

        var settings = new SettingsService(_paths);
        settings.Load();
        settings.AdoptLegacyProviderModels(Houses()).Should().BeFalse();

        settings.ModelFor("copilot").Should().Be("gpt-6-elegido-a-mano");
    }

    /// <summary>Las dos casas de verdad, que son quienes declaran su nombre viejo de ajuste.</summary>
    private static IReadOnlyList<IAuditorProvider> Houses()
        => new IAuditorProvider[] { new RealCopilotAgent(), new ClaudeCodeProvider(string.Empty) };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
