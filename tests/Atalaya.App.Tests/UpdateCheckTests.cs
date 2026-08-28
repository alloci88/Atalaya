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
/// hay, que <b>no diga nada cuando falla</b>, que no repita una versión ya descartada, y que no
/// pregunte más de una vez al día.
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

    private UpdateCheckService Service(HttpStub stub, string mine = "1.0.0", string? repoUrl = RepoUrl)
        => new(
            new DeployConfig { AppRepoUrl = repoUrl ?? string.Empty },
            _account,
            new GitHubApiClient(stub.Client()),
            _settings,
            log: null,
            now: () => _now,
            currentVersion: () => mine);

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

    // ---------------------------------------------------------------- una vez al día

    [Fact]
    public async Task No_se_pregunta_mas_de_una_vez_cada_24h()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        await service.CheckAsync(CancellationToken.None);
        _now = _now.AddHours(3);
        await service.CheckAsync(CancellationToken.None);

        stub.Requests.Should().ContainSingle("la segunda vez se contesta con lo que ya se sabía");
    }

    /// <summary>
    /// Y mientras va throttled el banner NO desaparece: se contesta con la última Release vista.
    /// Un aviso que se va y vuelve solo cada 24 h es un aviso en el que nadie confía.
    /// </summary>
    [Fact]
    public async Task Mientras_no_toca_preguntar_el_aviso_se_mantiene()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0", "https://github.com/o/r/releases/tag/v1.2.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        await service.CheckAsync(CancellationToken.None);
        _now = _now.AddHours(3);
        UpdateAvailability cached = await service.CheckAsync(CancellationToken.None);

        cached.HasUpdate.Should().BeTrue();
        cached.Version!.ToString().Should().Be("1.2.0");
        cached.Url.Should().Be("https://github.com/o/r/releases/tag/v1.2.0");
    }

    [Fact]
    public async Task Pasadas_24h_se_vuelve_a_preguntar()
    {
        var stub = new HttpStub().Json(ReleaseJson("v1.2.0")).Json(ReleaseJson("v1.3.0"));
        UpdateCheckService service = Service(stub, mine: "1.0.0");

        await service.CheckAsync(CancellationToken.None);
        _now = _now.AddHours(25);
        UpdateAvailability second = await service.CheckAsync(CancellationToken.None);

        stub.Requests.Should().HaveCount(2);
        second.Version!.ToString().Should().Be("1.3.0");
    }

    /// <summary>
    /// Un fallo NO gasta el cupo del día: quien arrancó sin red esta mañana se entera esta tarde.
    /// No degenera en machaqueo porque la consulta se hace una vez por arranque, no en bucle.
    /// </summary>
    [Fact]
    public async Task Un_fallo_no_consume_el_cupo_de_24h()
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

    // ---------------------------------------------------------------- la versión propia

    /// <summary>
    /// F8 §1: «Acerca de» enseña la versión del ENSAMBLADO, que es la que sella
    /// <c>Directory.Build.props</c> —y la que el workflow pisa con la del tag—. Si esto dejara de
    /// ser una versión legible, el chequeo no podría comparar nada.
    /// </summary>
    [Fact]
    public void La_version_de_Acerca_de_es_la_del_ensamblado_y_es_SemVer()
    {
        string version = AboutInfo.CurrentVersion();

        version.Should().NotBeNullOrWhiteSpace().And.NotBe("—");
        version.Should().NotContain("+", "los metadatos de build se recortan");
        SemanticVersion.TryParse(version).Should().NotBeNull();
    }

    [Fact]
    public void El_ensamblado_lleva_la_version_de_Directory_Build_props()
    {
        string informational = typeof(AboutInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        // El compilador le añade «+<sha>»; lo que «Acerca de» enseña es la parte de delante.
        informational.Split('+')[0].Should().Be(AboutInfo.CurrentVersion());
    }
}
