using System.Text;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Copilot;
using Atalaya.OpenAI;
using Atalaya.Tests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// PROV-3 §§2-3 — <b>la configuración del endpoint por API, y dónde acaba cada dato</b>.
/// <para>
/// Los siete casos de aquí son de regla, no de forma: ninguno mira que un control exista. Vigilan
/// que los cuatro datos de esta casa vayan cada uno a su sitio —tres a <c>settings.json</c> y la
/// clave al almacén cifrado—, que se guarden al cambiarlos, que el formulario solo aparezca con
/// esta casa elegida, que «Probar» no gaste una llamada mientras falte algo ni afirme nada de una
/// configuración que ya se ha tocado, y que un reset de fábrica se lleve la clave con lo demás.
/// </para>
/// <para>
/// <b>Todos comparten la misma forma de romperse: en silencio.</b> Una clave escrita en
/// <c>settings.json</c> no falla —funciona igual de bien, y se publica en la primera captura de
/// pantalla que alguien pegue en un chat—; una URL que no llega al fichero no se nota hasta la
/// auditoría siguiente; y un reset que se olvida de <c>secrets.dat</c> dice «como recién
/// instalada» mientras deja en el disco lo único que cuesta dinero.
/// </para>
/// </summary>
public sealed class OpenAiSettingsTests : IDisposable
{
    /// <summary>Una clave reconocible: si asoma en algún sitio, se ve a la primera.</summary>
    private const string Clave = "sk-clave-de-prueba-que-no-debe-salir-de-aqui";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly ProviderSecretStore _secrets;
    private readonly OpenAiEndpointSettings _openAi;

    /// <summary>La otra casa del desplegable: una que no toca ni red ni SDK.</summary>
    private readonly FakeCopilotAgent _otraCasa = new();

    public OpenAiSettingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-prov3", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _settings.Save(_settings.Current);   // que `settings.json` exista: es lo que se inspecciona
        _secrets = new ProviderSecretStore(_paths);
        _openAi = new OpenAiEndpointSettings(_settings, _secrets, Endpoints);
    }

    // ============================================================== §3 · dónde NO está la clave

    /// <summary>
    /// <b>La clave no se escribe en los ajustes, ni en lo que un log volcaría</b> (§3).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Nada, y ahí está el problema: guardarla con
    /// <c>SetProviderOption</c> «porque es un ajuste más de esa casa» funciona perfectamente —la
    /// aplicación auditaría igual— y deja la credencial de pago en un fichero de texto que se abre
    /// para mirar un ajuste, se pega en un mensaje cuando algo falla y acaba en una captura. No
    /// hay excepción, no hay test rojo y no hay forma de enterarse hasta que la usa otro.
    /// </para>
    /// <para>
    /// La segunda mitad es el camino del log: <see cref="OpenAiEndpoint"/> es un <c>record</c>, y
    /// un <c>record</c> imprime sus miembros. El día que la clave entrara en él, cualquier
    /// <c>logger.LogDebug("{Endpoint}", endpoint)</c> la publicaría sin que nadie lo hubiera
    /// escrito a propósito — y los logs son justamente lo que se manda cuando algo falla.
    /// </para>
    /// </summary>
    [Fact]
    public void La_clave_de_API_no_se_escribe_en_los_ajustes_ni_en_lo_que_un_log_volcaria()
    {
        SettingsViewModel vm = Model();
        vm.SelectedProviderId = OpenAiEndpointSettings.ProviderId;
        vm.OpenAiBaseUrl = "https://api.example.invalid/v1";
        vm.SelectedModelId = "modelo-x";

        vm.SaveApiKey(Clave);

        _secrets.Has(OpenAiEndpointSettings.ProviderId).Should().BeTrue(
            "la clave se guarda: lo que se mide es DÓNDE, no si se pierde");

        File.ReadAllText(_paths.SettingsJson).Should().NotContain(Clave,
            "`settings.json` es texto plano y se enseña; una clave ahí es una clave publicada");

        Encoding.Latin1.GetString(File.ReadAllBytes(_secrets.Location)).Should().NotContain(Clave,
            "y en su propio fichero tampoco está en claro: va cifrada con DPAPI de usuario");

        _openAi.Endpoint.ToString().Should().NotContain(Clave,
            "lo que se le pasa al transporte describe A QUIÉN se habla, no con qué credencial");
    }

    // ====================================================== §2 · cada dato, en su sitio, al tocarlo

    /// <summary>
    /// <b>Los tres ajustes del endpoint se guardan al cambiarlos, y cada uno donde le toca</b> (§2).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Un campo que se queda fuera de
    /// <see cref="SettingsViewModel.Editable"/> o del volcado se escribe en pantalla, se lee en
    /// pantalla y no llega al fichero: el usuario configura su endpoint, cierra Ajustes y la
    /// siguiente auditoría falla diciendo que falta la URL que él acaba de escribir. Es el defecto
    /// de R5 con otra ropa, y aquí es más caro porque los campos son nuevos.
    /// </para>
    /// <para>
    /// Y el modelo va al mapa <c>providerModels</c> <b>bajo el identificador de ESTA casa</b>: un
    /// sitio nuevo lo dejaría invisible para <c>ModelResolver</c>, y guardarlo bajo el de otra casa
    /// es el fallo que PROV-2 §2 ya arregló una vez.
    /// </para>
    /// </summary>
    [Fact]
    public void La_URL_el_modo_y_el_modelo_se_guardan_al_cambiarlos_cada_uno_en_su_sitio()
    {
        SettingsViewModel vm = Model();
        vm.SelectedProviderId = OpenAiEndpointSettings.ProviderId;

        vm.OpenAiBaseUrl = "http://localhost:11434/v1";
        vm.OpenAiAuthMode = OpenAiAuth.ApiKey;
        vm.SelectedModelId = "llama3.1:8b";

        // Sin pulsar nada: en el disco ya está, y en el sitio de cada uno.
        var releido = new SettingsService(_paths);
        releido.Load();

        releido.ProviderOption(OpenAiEndpointSettings.ProviderId, OpenAiSettingKeys.BaseUrl)
            .Should().Be("http://localhost:11434/v1");
        OpenAiSettingKeys.AuthOf(
                releido.ProviderOption(OpenAiEndpointSettings.ProviderId, OpenAiSettingKeys.Auth))
            .Should().Be(OpenAiAuth.ApiKey);
        releido.ModelFor(OpenAiEndpointSettings.ProviderId).Should().Be("llama3.1:8b",
            "el modelo va al mapa por proveedor de siempre, no a un sitio nuevo");

        vm.Saved[nameof(SettingsViewModel.OpenAiBaseUrl)].Shown.Should().BeTrue(
            "y la fila lo dice con su «Guardado ✓», como las demás");
    }

    // ========================================================= §2 · el formulario es de UNA casa

    /// <summary>
    /// <b>Las cuatro filas —y el desplegable de modelos— dependen de la casa elegida</b> (§2).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Las dos mitades fallan calladas y en direcciones
    /// opuestas. Enseñadas siempre, quien audita con otra casa ve una URL, una clave y un modo de
    /// autenticación que no gobiernan absolutamente nada — un control conectado a nada es peor que
    /// no tenerlo (D-275). Y al revés: esta casa no publica lista de modelos, así que dejar el
    /// desplegable sería dejarlo con una sola entrada que nadie puede cambiar, es decir, <b>sin
    /// forma de elegir modelo</b>; y eso no se ve hasta que alguien intenta configurarlo.
    /// </para>
    /// </summary>
    [Fact]
    public void El_formulario_del_endpoint_solo_se_ve_con_la_casa_por_API_elegida()
    {
        SettingsViewModel vm = Model();

        vm.SelectedProviderId = _otraCasa.ProviderId;
        vm.ShowOpenAiEndpoint.Should().BeFalse("con otra casa no hay endpoint que configurar");
        vm.ShowModelList.Should().BeTrue("y esa casa sí publica su lista de modelos");

        vm.SelectedProviderId = OpenAiEndpointSettings.ProviderId;
        vm.ShowOpenAiEndpoint.Should().BeTrue();
        vm.ShowModelList.Should().BeFalse(
            "esta casa no tiene lista: el modelo se escribe a mano, y dos controles para el mismo "
            + "valor —uno de ellos inservible— es peor que uno");
    }

    // ================================================================== §2 · «Probar»

    /// <summary>
    /// <b>«Probar» no gasta una llamada mientras falte algo, y dice qué falta</b> (§2).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Llamar igual: la respuesta sería la misma frase de «no
    /// se pudo conectar» treinta segundos después, tanto si la URL está mal escrita como si está
    /// vacía o como si el modelo no se ha puesto. El usuario no sabría cuál de las cuatro cajas
    /// mirar, que es exactamente para lo que existe este botón.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("", "modelo-x", true, "URL")]
    [InlineData("http://api.example.invalid/v1", "modelo-x", true, "http")]
    [InlineData("https://api.example.invalid/v1", "", true, "modelo")]
    [InlineData("https://api.example.invalid/v1", "modelo-x", false, "clave")]
    public async Task Probar_no_llama_al_endpoint_mientras_falte_algo_y_dice_cual(
        string url, string model, bool conClave, string esperado)
    {
        _openAi.SaveBaseUrl(url);
        _settings.SetModelFor(OpenAiEndpointSettings.ProviderId, model);
        if (conClave)
        {
            _openAi.SaveKey(Clave);
        }

        ChatProbe probe = await _openAi.ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        probe.Message.Should().ContainEquivalentOf(esperado,
            "la frase dice QUÉ revisar, no «error de configuración»");
        _pedido.Should().BeEmpty("no se gasta una llamada para descubrir lo que ya se sabía");
    }

    /// <summary>
    /// <b>«Probar» habla con lo que está configurado, y enseña la respuesta tal cual</b> (§2).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Probar contra otra cosa: una URL sin recortar, el
    /// modelo que tenía elegido otra casa o —el peor— una clave que no es la guardada. El botón
    /// diría «conecta» sobre una configuración que no es la que se va a usar, y quien lo pulsó
    /// cerraría Ajustes convencido. Un «Probar» que no prueba lo mismo que se lanza es peor que no
    /// tenerlo, porque además da confianza.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Probar_habla_con_lo_configurado_y_ensena_la_respuesta_en_linea()
    {
        SettingsViewModel vm = Model();
        vm.SelectedProviderId = OpenAiEndpointSettings.ProviderId;
        vm.OpenAiBaseUrl = "https://api.example.invalid/v1";
        vm.OpenAiAuthMode = OpenAiAuth.ApiKey;
        vm.SelectedModelId = "modelo-x";
        vm.SaveApiKey(Clave);

        _respuesta = new ChatProbe(true, "Conecta y el modelo «modelo-x» existe.", "HTTP 200");

        await vm.TestEndpointCommand.ExecuteAsync(null);

        _pedido.Should().ContainSingle().Which.Should().Be(
            (new OpenAiEndpoint("https://api.example.invalid/v1", "modelo-x", OpenAiAuth.ApiKey), Clave),
            "se prueba la configuración que se va a usar, entera: URL, modelo, modo de "
            + "autenticación y la clave guardada");

        vm.ProbeSucceeded.Should().BeTrue();
        vm.ProbeMessage.Should().Be("Conecta y el modelo «modelo-x» existe.",
            "la frase la redacta el transporte y la pantalla la enseña sin reescribirla");
        vm.ProbeDetail.Should().Be("HTTP 200");
    }

    /// <summary>
    /// <b>Tocar la configuración borra el resultado anterior</b> (§2).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Un «conecta» en verde debajo de una URL que se acaba de
    /// cambiar afirma de la configuración de ahora lo que se midió de la de antes. No falla nada,
    /// no hay aviso, y es justo el caso en que alguien mira ese renglón: después de corregir algo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Tocar_la_configuracion_borra_el_resultado_de_la_prueba_anterior()
    {
        SettingsViewModel vm = Model();
        vm.SelectedProviderId = OpenAiEndpointSettings.ProviderId;
        vm.OpenAiBaseUrl = "https://api.example.invalid/v1";
        vm.SelectedModelId = "modelo-x";
        vm.SaveApiKey(Clave);
        _respuesta = new ChatProbe(true, "Conecta.");
        await vm.TestEndpointCommand.ExecuteAsync(null);
        vm.ProbeSucceeded.Should().BeTrue();

        vm.OpenAiBaseUrl = "https://otro.example.invalid/v1";

        vm.ProbeSucceeded.Should().BeFalse();
        vm.ProbeMessage.Should().BeEmpty(
            "el resultado hablaba de la configuración de antes, y ya no está en pantalla");
    }

    // ================================================== §3 · el reset se lleva también la clave

    /// <summary>
    /// <b>El reset de fábrica se lleva <c>secrets.dat</c></b> (§3).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> El reset borra el clon, los ajustes y la cuenta y dice
    /// «Atalaya arrancará como recién instalada». Sin esa línea la frase sería falsa justo en lo
    /// único que cuesta dinero: la clave de API seguiría en el disco —cifrada para ese usuario de
    /// Windows y perfectamente utilizable por él— después de que alguien pulsara el botón que
    /// existe para no dejar nada suyo en la máquina.
    /// </para>
    /// </summary>
    [Fact]
    public void El_reset_de_fabrica_se_lleva_la_clave_de_API()
    {
        string remote = Path.Combine(_root, "remote.git");
        TestGit.Init(remote, isBare: true);

        GitHubAccountService account = TestFactory.Account(_paths);
        account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        var hub = new HubContext(
            _paths, _settings, account,
            new DeployConfig { HubUrl = remote },
            NullLoggerFactory.Instance);
        hub.EnsureHub();
        hub.Sync!.CommitAndPush("hub: alta").Should().BeTrue(hub.Sync!.Why());

        _openAi.SaveKey(Clave);
        File.Exists(_secrets.Location).Should().BeTrue("hay clave que borrar");

        var reset = new FactoryResetService(
            hub, _paths, _settings, account, new OpenSessionStore(_paths), _secrets);

        reset.Reset("ana").Done.Should().BeTrue();

        File.Exists(_secrets.Location).Should().BeFalse(
            "la clave de API es de esta máquina y se va con `auth.dat` y `settings.json`");
        new ProviderSecretStore(_paths).Has(OpenAiEndpointSettings.ProviderId).Should().BeFalse();
    }

    // ================================================================================ andamiaje

    /// <summary>Lo que se le pidió al transporte, en orden. Vacío = no se llamó a nadie.</summary>
    private readonly List<(OpenAiEndpoint Endpoint, string? Key)> _pedido = new();

    /// <summary>Lo que contesta el endpoint de mentira.</summary>
    private ChatProbe _respuesta = new(true, "Conecta.");

    /// <summary>
    /// La fábrica que en la aplicación monta el transporte de verdad. Aquí apunta el hueco que
    /// PROV-3 deja abierto: la pantalla programa contra <see cref="IChatEndpoint"/> y nada más.
    /// </summary>
    private IChatEndpoint Endpoints(OpenAiEndpoint endpoint, string? key)
    {
        _pedido.Add((endpoint, key));
        return new EndpointDeMentira(_respuesta);
    }

    private sealed class EndpointDeMentira : IChatEndpoint
    {
        private readonly ChatProbe _probe;

        public EndpointDeMentira(ChatProbe probe) => _probe = probe;

        public Task<ChatTurn> SendAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ChatToolSpec> tools,
            Action<string>? onText,
            CancellationToken ct)
            => throw new NotSupportedException("«Probar» no manda turnos.");

        public Task<ChatProbe> ProbeAsync(CancellationToken ct) => Task.FromResult(_probe);
    }

    /// <summary>
    /// La página con DOS casas entre las que elegir, que es lo que hace que
    /// <c>SelectedProviderId</c> signifique algo y que el modelo se guarde bajo el identificador
    /// correcto.
    /// </summary>
    private SettingsViewModel Model()
    {
        HubContext hub = TestFactory.Hub(_paths, _settings);
        var porApi = new OpenAiCompatibleProvider(
            endpoint: () => _openAi.Endpoint,
            key: () => null);

        return new SettingsViewModel(
            _settings,
            _otraCasa,
            new ToastCenter(),
            new FactoryResetService(
                hub, _paths, _settings, TestFactory.Account(_paths), new OpenSessionStore(_paths),
                _secrets),
            new NoReset(),
            hub,
            new NavigationService(new NoServices()),
            new AuditorProviderRegistry(_settings, new IAuditorProvider[] { _otraCasa, porApi }),
            openAi: _openAi);
    }

    private sealed class NoReset : IFactoryResetConfirmer
    {
        public bool Confirm(FactoryResetConfirmation confirmation) => false;
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un fichero bloqueado no puede tumbar la suite.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
