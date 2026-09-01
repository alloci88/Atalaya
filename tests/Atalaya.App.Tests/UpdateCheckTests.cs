using System.Net;
using System.Reflection;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F8 §3 — el aviso de versión nueva. Lo que se comprueba aquí es lo que hace que un chequeo de
/// cortesía sea cortés: que avise cuando de verdad hay algo más nuevo, que no avise cuando no lo
/// hay, que <b>no diga nada cuando falla</b>, que no repita una versión ya descartada, y que
/// pregunte a un ritmo razonable: en cada arranque con un suelo de 15 minutos, y cada 24 h en una
/// instancia que lleva abierta.
/// </summary>
public sealed class UpdateCheckTests : IDisposable
{
    private const string RepoUrl = "https://github.com/Applied-Advanced-Solutions-AAS/Atalaya";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;
    private DateTimeOffset _now = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

    public UpdateCheckTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-update", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _account = TestFactory.Account(_paths);
        _account.Connect("token-de-la-cuenta", new GitHubUser(1, "alguien", "Alguien", null, null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static string ReleaseJson(string tag, string? url = "https://github.com/x/y/releases/tag/v9")
        => $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","name":"Atalaya {{tag}}"}""";

    private UpdateCheckService Service(
        HttpStub stub, string mine = "1.0.0", string? repoUrl = RepoUrl, SettingsService? settings = null)
        => new(
            new DeployConfig { AppRepoUrl = repoUrl ?? string.Empty },
            _account,
            new GitHubApiClient(stub.Client()),
            settings ?? _settings,
            log: null,
            now: () => _now,
            currentVersion: () => mine);

    /// <summary>
    /// Un arranque nuevo de verdad: servicio nuevo y ajustes <b>releidos del disco</b>. Lo unico
    /// que cruza es lo que Atalaya guardo, que es exactamente lo que cruza un reinicio real.
    /// </summary>
    private UpdateCheckService TrasReiniciar(HttpStub stub, string mine = "1.0.0")
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        return Service(stub, mine, settings: settings);
    }

    // ---------------------------------------------------------------- hay / no hay versión nueva

    [Fact]
    public async Task Una_release_mas_nueva_produce_aviso_con_su_pagina()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0", "https://github.com/o/r/releases/tag/v1.2.0"));

        UpdateAvailability result = await Service(stub, mine: "1.0.0").CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeTrue();
        result.Version!.ToString().Should().Be("1.2.0");
        result.Url.Should().Be("https://github.com/o/r/releases/tag/v1.2.0");
    }

    [Fact]
    public async Task La_misma_version_no_avisa_de_nada()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.0.0"));

        UpdateAvailability result = await Service(stub, mine: "1.0.0").CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
        result.Reason.Should().Contain("al día");
    }

    /// <summary>
    /// Un binario más nuevo que la última Release es lo normal en la máquina de quien desarrolla.
    /// Avisar ahí sería decirle que se «actualice» hacia atrás.
    /// </summary>
    [Fact]
    public async Task Una_release_mas_vieja_no_avisa_de_nada()
    {
        var stub = new HttpStub().Json(ReleaseJson("v0.9.0"));

        UpdateAvailability result = await Service(stub, mine: "1.0.0").CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
    }

    // ---------------------------------------------------------------- fallar en silencio

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Si_la_api_falla_no_hay_aviso_y_no_revienta(HttpStatusCode status)
    {
        var stub = new HttpStub().Status(status);

        UpdateAvailability result = await Service(stub).CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
        result.Reason.Should().NotBeEmpty("el motivo va al log, aunque no se enseñe");
    }

    [Fact]
    public async Task Sin_red_no_hay_aviso_y_no_revienta()
    {
        var stub = new HttpStub().Throws(new System.Net.Http.HttpRequestException("sin red"));

        UpdateAvailability result = await Service(stub).CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
    }

    /// <summary>
    /// Un repositorio sin Releases contesta 404, y eso NO es un error: es «todavía no hay
    /// ninguna». Se sella igual, porque es una respuesta.
    /// </summary>
    [Fact]
    public async Task Un_repo_sin_releases_no_avisa_y_cuenta_como_consulta_hecha()
    {
        var stub = new HttpStub().Status(HttpStatusCode.NotFound);

        UpdateAvailability result = await Service(stub).CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
        _settings.Current.LastUpdateCheckUtc.Should().Be(_now);
    }

    [Fact]
    public async Task Sin_appRepoUrl_no_se_pregunta_nada()
    {
        var stub = new HttpStub();

        UpdateAvailability result = await Service(stub, repoUrl: null).CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
        stub.Requests.Should().BeEmpty("sin repositorio declarado no se gasta ni una llamada");
    }

    [Fact]
    public async Task Sin_cuenta_conectada_no_se_pregunta_nada()
    {
        _account.Disconnect();
        var stub = new HttpStub();

        UpdateAvailability result = await Service(stub).CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
        stub.Requests.Should().BeEmpty();
    }

    /// <summary>El token de la cuenta, y ninguna credencial nueva: es el anti-objetivo de F8.</summary>
    [Fact]
    public async Task La_consulta_viaja_con_el_token_de_la_cuenta()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0"));

        await Service(stub).CheckAsync(CancellationToken.None);

        stub.Requests.Should().ContainSingle();
        stub.Requests[0].Headers.Authorization!.Parameter.Should().Be("token-de-la-cuenta");
        stub.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/repos/Applied-Advanced-Solutions-AAS/Atalaya/releases/latest");
    }

    // ---------------------------------------------------------------- descarte

    [Fact]
    public async Task Una_version_descartada_no_vuelve_a_avisar()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0")).Json(ReleaseJson("v1.2.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        UpdateAvailability first = await service.CheckAsync(CancellationToken.None);
        first.HasUpdate.Should().BeTrue();
        service.Dismiss(first.Version);

        _now = _now.AddDays(2);
        UpdateAvailability second = await service.CheckAsync(CancellationToken.None);

        second.HasUpdate.Should().BeFalse();
        second.Reason.Should().Contain("descartada");
    }

    /// <summary>
    /// Descartar es «ya me he enterado de ESTA», no «no me avises nunca más». La siguiente sí
    /// tiene que llegar, o el aviso se apagaría para siempre con un clic.
    /// </summary>
    [Fact]
    public async Task Descartar_una_version_no_calla_la_siguiente()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0")).Json(ReleaseJson("v1.3.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        UpdateAvailability first = await service.CheckAsync(CancellationToken.None);
        service.Dismiss(first.Version);

        _now = _now.AddDays(2);
        UpdateAvailability second = await service.CheckAsync(CancellationToken.None);

        second.HasUpdate.Should().BeTrue();
        second.Version!.ToString().Should().Be("1.3.0");
    }

    /// <summary>
    /// Descartar y la frecuencia son cosas distintas: que ahora se pregunte en cada arranque no
    /// puede resucitar un aviso que alguien ya cerró. Aquí la consulta SÍ se hace —dos peticiones—
    /// y aun así no hay aviso.
    /// </summary>
    [Fact]
    public async Task Una_descartada_sigue_callada_aunque_el_chequeo_vuelva_a_correr()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0")).Json(ReleaseJson("v1.2.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        UpdateAvailability first = await service.CheckAsync(CancellationToken.None);
        service.Dismiss(first.Version);

        _now = _now.AddMinutes(20);
        UpdateAvailability afterRestart = await TrasReiniciar(stub, mine: "1.0.0")
            .CheckAsync(CancellationToken.None);

        stub.Requests.Should().HaveCount(2, "el arranque vuelve a preguntar");
        afterRestart.HasUpdate.Should().BeFalse();
        afterRestart.Reason.Should().Contain("descartada");
    }

    // ------------------------------------------------- en cada arranque, con suelo de 15 minutos

    /// <summary>
    /// La regla nueva, y la razón de todo esto: <b>reiniciar tras publicar una release basta para
    /// ver el aviso</b>, sin tocar ningún fichero de caché. Antes había que esperar a que venciera
    /// la cuota del día —o editarla a mano, que es lo que hacía la función imposible de probar—.
    /// </summary>
    [Fact]
    public async Task Reiniciar_tras_publicar_una_release_ensena_el_aviso()
    {
        var alDia = new HttpStub().Json(ReleaseJson("v1.0.0"));
        UpdateAvailability antes = await TrasReiniciar(alDia, mine: "1.0.0").CheckAsync(CancellationToken.None);
        antes.HasUpdate.Should().BeFalse("cuando se miró no había nada más nuevo");

        // Se publica la v1.1.0 y el usuario reinicia Atalaya veinte minutos después.
        _now = _now.AddMinutes(20);
        var publicada = new HttpStub().Json(ReleaseJson("v1.1.0"));

        UpdateAvailability despues = await TrasReiniciar(publicada, mine: "1.0.0")
            .CheckAsync(CancellationToken.None);

        publicada.Requests.Should().ContainSingle("el arranque vuelve a preguntar, no espera a mañana");
        despues.HasUpdate.Should().BeTrue();
        despues.Version!.ToString().Should().Be("1.1.0");
    }

    /// <summary>
    /// El suelo anti-bucle, que es lo único que el límite tiene que impedir: abrir y cerrar la
    /// aplicación seguido no dispara una consulta por vez. Y el sello sobrevive al reinicio, o el
    /// suelo no existiría.
    /// </summary>
    [Fact]
    public async Task Dentro_del_suelo_un_arranque_no_vuelve_a_preguntar()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0"));

        await TrasReiniciar(stub, mine: "1.0.0").CheckAsync(CancellationToken.None);
        _now = _now.AddMinutes(5);
        await TrasReiniciar(stub, mine: "1.0.0").CheckAsync(CancellationToken.None);

        stub.Requests.Should().ContainSingle("la segunda vez se contesta con lo que ya se sabía");
    }

    [Fact]
    public async Task Pasado_el_suelo_un_arranque_vuelve_a_preguntar()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0")).Json(ReleaseJson("v1.3.0"));

        await TrasReiniciar(stub, mine: "1.0.0").CheckAsync(CancellationToken.None);
        _now = _now.AddMinutes(16);
        UpdateAvailability second = await TrasReiniciar(stub, mine: "1.0.0").CheckAsync(CancellationToken.None);

        stub.Requests.Should().HaveCount(2);
        second.Version!.ToString().Should().Be("1.3.0");
    }

    /// <summary>Los dos números de la política, escritos una sola vez y comprobados aquí.</summary>
    [Fact]
    public void El_suelo_son_quince_minutos_y_el_re_chequeo_veinticuatro_horas()
    {
        UpdateCheckService.MinimumInterval.Should().Be(TimeSpan.FromMinutes(15));
        UpdateCheckService.CheckInterval.Should().Be(TimeSpan.FromHours(24));
    }

    /// <summary>
    /// Y mientras no toca preguntar el banner NO desaparece: se contesta con la última Release
    /// vista. Un aviso que se va y vuelve solo es un aviso en el que nadie confía.
    /// </summary>
    [Fact]
    public async Task Mientras_no_toca_preguntar_el_aviso_se_mantiene()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0", "https://github.com/o/r/releases/tag/v1.2.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        await service.CheckAsync(CancellationToken.None);
        _now = _now.AddMinutes(5);
        UpdateAvailability cached = await service.CheckAsync(CancellationToken.None);

        cached.HasUpdate.Should().BeTrue();
        cached.Version!.ToString().Should().Be("1.2.0");
        cached.Url.Should().Be("https://github.com/o/r/releases/tag/v1.2.0");
    }

    /// <summary>
    /// Un fallo NO gasta el turno: quien arrancó sin red se entera en cuanto vuelva. No degenera
    /// en machaqueo porque la consulta se hace una vez por arranque, no en bucle.
    /// </summary>
    [Fact]
    public async Task Un_fallo_no_consume_el_suelo()
    {
        var stub = new HttpStub()
            .Throws(new System.Net.Http.HttpRequestException("sin red"))
            .Json(ReleaseJson("v1.2.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        await service.CheckAsync(CancellationToken.None);
        _settings.Current.LastUpdateCheckUtc.Should().BeNull("un fallo no es una consulta hecha");

        _now = _now.AddMinutes(5);
        UpdateAvailability retry = await service.CheckAsync(CancellationToken.None);

        retry.HasUpdate.Should().BeTrue();
    }

    // ------------------------------------------------- la instancia que lleva abierta: cada 24 h

    /// <summary>
    /// Quien no reinicia no tiene arranque que le dispare el chequeo, así que sigue habiendo un
    /// re-chequeo periódico —y sigue siendo de 24 h—. Preguntar si toca no cuesta una llamada:
    /// eso es lo que hace el temporizador en cada tick.
    /// </summary>
    [Fact]
    public async Task Una_instancia_abierta_no_vuelve_a_preguntar_antes_de_24h()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");
        await service.CheckAsync(CancellationToken.None);

        service.PeriodicRecheckDue().Should().BeFalse("acaba de mirar");
        _now = _now.AddHours(23);
        service.PeriodicRecheckDue().Should().BeFalse("23 h no son 24");
    }

    [Fact]
    public async Task Una_instancia_abierta_vuelve_a_preguntar_a_las_24h()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0")).Json(ReleaseJson("v1.3.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");
        await service.CheckAsync(CancellationToken.None);

        _now = _now.AddHours(24);
        service.PeriodicRecheckDue().Should().BeTrue();
        UpdateAvailability second = await service.CheckAsync(CancellationToken.None);

        stub.Requests.Should().HaveCount(2);
        second.Version!.ToString().Should().Be("1.3.0");
    }

    /// <summary>
    /// Y el re-chequeo cuenta desde el último INTENTO, no desde el último acierto. Con el sello de
    /// los ajustes —que solo avanza cuando hay red— una instancia sin red habría vuelto a
    /// preguntar en cada tick del sondeo, que es justo el machaqueo que no se quiere.
    /// </summary>
    [Fact]
    public async Task Una_instancia_sin_red_no_reintenta_en_cada_tick()
    {
        var stub = new HttpStub().Throws(new System.Net.Http.HttpRequestException("sin red"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");
        await service.CheckAsync(CancellationToken.None);

        _now = _now.AddMinutes(1);
        service.PeriodicRecheckDue().Should().BeFalse("el intento cuenta aunque no haya salido bien");
    }

    /// <summary>
    /// Y el cableado, de punta a punta: el temporizador de la ventana llama a la carcasa en cada
    /// tick, la carcasa pregunta si toca, y solo a las 24 h se vuelve a consultar. Sin esto, la
    /// regla estaría escrita y no la llamaría nadie.
    /// </summary>
    [Fact]
    public async Task La_carcasa_abierta_solo_vuelve_a_preguntar_a_las_24h()
    {
        using var repo = new DriftRepo();
        GitHubAccountService account = TestFactory.Account(repo.Paths);
        account.Connect("token-de-la-cuenta", new GitHubUser(1, "alguien", "Alguien", null, null));

        var stub = new HttpStub().Json(ReleaseJson("v1.2.0")).Json(ReleaseJson("v1.3.0"));
        var updates = new UpdateCheckService(
            new DeployConfig { AppRepoUrl = RepoUrl },
            account,
            new GitHubApiClient(stub.Client()),
            repo.Settings,
            log: null,
            now: () => _now,
            currentVersion: () => "1.0.0");
        MainViewModel shell = TestFactory.Shell(repo.Paths, repo.Hub, updates: updates);

        await shell.CheckForUpdatesAsync();
        shell.UpdateAvailable.Should().BeTrue("el arranque ya preguntó");

        _now = _now.AddHours(1);
        await shell.RecheckForUpdatesIfDueAsync();
        stub.Requests.Should().ContainSingle("un tick cualquiera no gasta una consulta");

        _now = _now.AddHours(23);
        await shell.RecheckForUpdatesIfDueAsync();

        stub.Requests.Should().HaveCount(2, "a las 24 h la instancia abierta vuelve a mirar");
        shell.UpdateLabel.Should().Contain("1.3.0");
    }

    // ---------------------------------------------------------------- la versión propia

    /// <summary>
    /// F8 §1: «Acerca de» enseña la versión del ENSAMBLADO. Si esto dejara de ser una versión
    /// legible, el chequeo no podría comparar nada.
    /// </summary>
    [Fact]
    public void La_version_de_Acerca_de_es_la_del_ensamblado_y_es_SemVer()
    {
        string version = AboutInfo.CurrentVersion();

        version.Should().NotBeNullOrWhiteSpace().And.NotBe("—");
        SemanticVersion.TryParse(version).Should().NotBeNull();
        SemanticVersion.TryParse(AboutInfo.BaseVersion(version)).Should().NotBeNull(
            "es la que compara el chequeo");
    }

    [Fact]
    public void El_ensamblado_lleva_la_version_informativa_cruda()
    {
        string informational = typeof(AboutInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        // BUGFIX-VERSION: se lee ENTERA, con sufijos y metadatos. Recortar el «+sha» antes de
        // tiempo era perder justo la pieza que identifica de qué build viene un fallo.
        AboutInfo.CurrentVersion().Should().Be(informational.Trim());
    }

    // ================================================================ BUGFIX-VERSION · build local

    /// <summary>
    /// Un build local va POR DELANTE de la release cuyo tag lleva de base, no por detrás. Sin
    /// comparar con la base, SemVer haría de «1.0.3-dev» un pre-release de 1.0.3 y le anunciaría
    /// al usuario que descargue lo que ya tiene.
    /// </summary>
    [Fact]
    public async Task Un_build_local_no_anuncia_la_release_de_su_propio_tag()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.0.3"));

        UpdateAvailability result = await Service(stub, mine: "1.0.3-dev+0f920d9")
            .CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse("ya la tiene y va por delante");
        result.Reason.Should().Contain("al día");
    }

    /// <summary>Y con más commits encima, lo mismo.</summary>
    [Fact]
    public async Task Tampoco_con_commits_por_encima_del_tag()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.0.3"));

        UpdateAvailability result = await Service(stub, mine: "1.0.3-dev.4+abc1234")
            .CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
    }

    /// <summary>Pero cuando salga la siguiente, sí avisa: la marca de desarrollo no calla el aviso.</summary>
    [Fact]
    public async Task Un_build_local_si_avisa_de_la_version_siguiente()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.0.4"));

        UpdateAvailability result = await Service(stub, mine: "1.0.3-dev+0f920d9")
            .CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeTrue("la 1.0.4 no la tiene");
        result.Version!.ToString().Should().Be("1.0.4");
    }

    /// <summary>Y una release instalada sigue comparándose como siempre: avisa de la siguiente…</summary>
    [Fact]
    public async Task Una_release_instalada_avisa_de_la_siguiente()
        => (await Service(new HttpStub().Json(ReleaseJson("v1.0.4")), mine: "1.0.3")
                .CheckAsync(CancellationToken.None))
            .HasUpdate.Should().BeTrue();

    /// <summary>…y calla cuando ya tiene la última.</summary>
    [Fact]
    public async Task Una_release_instalada_calla_con_la_ultima()
        => (await Service(new HttpStub().Json(ReleaseJson("v1.0.3")), mine: "1.0.3")
                .CheckAsync(CancellationToken.None))
            .HasUpdate.Should().BeFalse();
}
