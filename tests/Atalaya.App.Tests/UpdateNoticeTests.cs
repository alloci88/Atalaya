using System.Net;
using Atalaya.App.Services;
using Atalaya.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-AVISO — lo que el banner DICE.
/// <para>
/// El defecto no estaba en el chequeo: la API devolvió bien el tag <c>v1.0.4</c> y la decisión fue
/// correcta («hay versión nueva: 1.0.4 (tienes 1.0.3)», en el log). Lo que fallaba era el texto,
/// que pasaba la versión por un segundo formateador y anunciaba «1.0» — un número que no es ni el
/// que tienes ni el que hay, y que se lee como 1.0.0.
/// </para>
/// <para>
/// Por eso estos tests miran la <b>frase</b>, y no el número parseado: entre «la decisión es
/// correcta» y «el usuario lee algo correcto» cabía una release entera.
/// </para>
/// </summary>
public sealed class UpdateNoticeTests : IDisposable
{
    private const string RepoUrl = "https://github.com/Applied-Advanced-Solutions-AAS/Atalaya";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;

    public UpdateNoticeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-aviso", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _account = TestFactory.Account(_paths);
        _account.Connect("token", new GitHubUser(1, "alguien", "Alguien", null, null));
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

    private static string ReleaseJson(string tag)
        => $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/o/r/releases/tag/{{tag}}"}""";

    private UpdateCheckService Service(HttpStub stub, string mine)
        => new(
            new DeployConfig { AppRepoUrl = RepoUrl },
            _account,
            new GitHubApiClient(stub.Client()),
            _settings,
            log: null,
            now: () => new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            currentVersion: () => mine);

    private async Task<UpdateAvailability> Check(string tag, string mine)
        => await Service(new HttpStub().Json(ReleaseJson(tag)), mine).CheckAsync(CancellationToken.None);

    // ------------------------------------------------------------------ el caso del parte

    /// <summary>
    /// El caso exacto reportado: 1.0.3 corriendo, v1.0.4 publicada. Antes decía «Atalaya 1.0
    /// disponible»; ahora dice las dos versiones enteras.
    /// </summary>
    [Fact]
    public async Task El_aviso_dice_la_version_publicada_entera_y_la_que_corre()
    {
        UpdateAvailability result = await Check("v1.0.4", mine: "1.0.3");

        result.HasUpdate.Should().BeTrue();
        result.Headline.Should().Be("Tienes la 1.0.3 · disponible la 1.0.4");
    }

    /// <summary>
    /// La regresión concreta, dicha como regresión: el número de la release aparece ENTERO, con su
    /// parche. «1.0» habría pasado el test de «contiene 1.0», así que se comprueba lo que no debe
    /// estar tanto como lo que sí.
    /// </summary>
    [Fact]
    public async Task El_parche_de_la_version_publicada_no_se_oculta()
    {
        UpdateAvailability result = await Check("v1.0.4", mine: "1.0.3");

        result.Headline.Should().Contain("1.0.4");
        result.Headline.Should().NotMatchRegex(@"disponible la 1\.0$");
        result.Headline.Should().NotContain("1.0.0", "1.0.0 no es ni la que corre ni la que hay");
    }

    /// <summary>
    /// Y el otro fallo posible del mismo texto: que las dos versiones estén cambiadas de sitio.
    /// Se comprueba el ORDEN, no solo que ambas aparezcan.
    /// </summary>
    [Fact]
    public async Task Las_dos_versiones_no_estan_intercambiadas()
    {
        UpdateAvailability result = await Check("v2.1.0", mine: "1.4.7");

        result.Headline.Should().Be("Tienes la 1.4.7 · disponible la 2.1.0");
        result.Headline.IndexOf("1.4.7", StringComparison.Ordinal)
            .Should().BeLessThan(result.Headline.IndexOf("2.1.0", StringComparison.Ordinal));
    }

    /// <summary>
    /// La frase la construye el resultado con las MISMAS versiones que usó para decidir. Es lo que
    /// hace imposible que el texto y la decisión discrepen.
    /// </summary>
    [Fact]
    public async Task El_texto_sale_de_las_mismas_versiones_que_decidieron()
    {
        UpdateAvailability result = await Check("v1.0.4", mine: "1.0.3");

        result.Current!.ToString().Should().Be("1.0.3");
        result.Version!.ToString().Should().Be("1.0.4");
        result.Headline.Should().Be($"Tienes la {result.Current} · disponible la {result.Version}");
    }

    // ------------------------------------------------------------------ cuando no hay aviso

    [Theory]
    [InlineData("v1.0.3")]
    [InlineData("v1.0.2")]
    [InlineData("v0.9.9")]
    public async Task Una_release_igual_o_anterior_no_produce_aviso(string tag)
    {
        UpdateAvailability result = await Check(tag, mine: "1.0.3");

        result.HasUpdate.Should().BeFalse();
        result.Headline.Should().BeEmpty("sin aviso no hay frase que enseñar");
    }

    // ------------------------------------------------------------------ builds locales

    /// <summary>
    /// Un build local con una release posterior <b>sí recibe aviso</b>, y la frase es la misma:
    /// las dos versiones enteras. Quien corre un `dist` de desarrollo es justo quien necesita
    /// enterarse de que salió una release — así se encontró este defecto.
    /// <para>
    /// La comparación usa la versión BASE (D-729), así que un <c>1.0.3-dev.5</c> se anuncia como
    /// 1.0.3: es el número con el que se decidió, y el banner enseña lo que decidió.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_build_local_con_release_posterior_avisa_con_las_dos_versiones()
    {
        UpdateAvailability result = await Check("v1.0.4", mine: "1.0.3-dev.5+ffc63d8");

        result.HasUpdate.Should().BeTrue();
        result.Headline.Should().Be("Tienes la 1.0.3 · disponible la 1.0.4");
    }

    /// <summary>
    /// Pero NO se le ofrece el botón, y se dice por qué. El aviso es informativo y sin acción: lo
    /// que no puede pasar es ofrecer algo que luego no está (F11 D-737).
    /// </summary>
    [Fact]
    public void Un_build_local_recibe_el_aviso_pero_no_la_accion()
    {
        string appDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, SelfUpdateService.RunnerExe), "relevo");

        var service = new SelfUpdateService(
            _paths,
            new DeployConfig { AppRepoUrl = RepoUrl },
            _account,
            new GitHubApiClient(new HttpStub().Client()),
            new AgentBusyGate(),
            new FixSnapshotStore(_paths),
            new UpdateJournal(_paths),
            log: null,
            currentVersion: () => "1.0.3-dev.5+ffc63d8",
            appDirectory: appDir);

        UpdateReadiness readiness = service.CanOffer();

        readiness.CanUpdate.Should().BeFalse();
        readiness.Reason.Should().Contain("build local");
        readiness.Reason.Should().Contain("recompilando", "y se dice qué hacer en su lugar");
    }

    /// <summary>Un build local por delante de la release publicada sigue callando (D-729).</summary>
    [Fact]
    public async Task Un_build_local_por_delante_de_la_release_no_avisa()
    {
        UpdateAvailability result = await Check("v1.0.3", mine: "1.0.3-dev.5+ffc63d8");

        result.HasUpdate.Should().BeFalse();
    }

    // ------------------------------------------------------------------ el chequeo, intacto

    /// <summary>
    /// El mecanismo NO se ha tocado: un fallo sigue sin decir nada en pantalla, que es lo que hace
    /// cortés a un chequeo de cortesía (D-622).
    /// </summary>
    [Fact]
    public async Task Un_fallo_sigue_sin_producir_frase()
    {
        var stub = new HttpStub().Status(HttpStatusCode.InternalServerError);

        UpdateAvailability result = await Service(stub, "1.0.3").CheckAsync(CancellationToken.None);

        result.HasUpdate.Should().BeFalse();
        result.Headline.Should().BeEmpty();
    }

    /// <summary>
    /// Y la respuesta cacheada de las 24 h produce la MISMA frase que la recién consultada. Era la
    /// tercera sospecha del parte: se descarta con un test, no con una lectura.
    /// </summary>
    [Fact]
    public async Task La_respuesta_cacheada_dice_lo_mismo_que_la_consultada()
    {
        UpdateAvailability fresh = await Check("v1.0.4", mine: "1.0.3");

        // Segundo arranque dentro de las 24 h: sin red que valga, se contesta con lo guardado.
        var offline = new HttpStub();
        UpdateAvailability cached = await Service(offline, "1.0.3").CheckAsync(CancellationToken.None);

        cached.Headline.Should().Be(fresh.Headline);
        cached.Version!.ToString().Should().Be("1.0.4");
        cached.Current!.ToString().Should().Be("1.0.3");
        offline.Requests.Should().BeEmpty("no se vuelve a preguntar dentro de las 24 h");
    }
}
