using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Copilot;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.1 — los dos ajustes nuevos: el tope de pasadas del barrido (que antes solo vivía en
/// <c>app.json</c> y no se podía tocar) y el selector de modelo, poblado con lo que el SDK lista
/// para la cuenta, nunca con una lista escrita a mano.
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly ToastCenter _toasts = new();

    public SettingsViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-ajustes", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _settings = new SettingsService(_paths);
        _settings.Load();
    }

    private SettingsViewModel NewViewModel(
        IAuditorProvider? agent = null, IFactoryResetConfirmer? confirmer = null)
    {
        HubContext hub = TestFactory.Hub(_paths, _settings);
        return new SettingsViewModel(
            _settings,
            agent ?? new FakeCopilotAgent(),
            _toasts,
            new FactoryResetService(
                hub, _paths, _settings, TestFactory.Account(_paths), new OpenSessionStore(_paths)),
            confirmer ?? new NeverConfirms(),
            hub,
            new NavigationService(new EmptyServices()));
    }

    /// <summary>El confirmador que dice que no: el reset de fábrica no se dispara sin querer.</summary>
    private sealed class NeverConfirms : IFactoryResetConfirmer
    {
        public bool Confirm(FactoryResetConfirmation confirmation) => false;
    }

    /// <summary>La navegación no se ejercita en estos casos; basta con que exista.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // ---------- 1. Tope de pasadas ----------

    [Fact]
    public void The_sweep_cap_is_editable_and_survives_a_reload()
    {
        SettingsViewModel vm = NewViewModel();
        vm.MaxPassesPerUnit.Should().Be(5, "el valor por defecto de D-095");

        vm.MaxPassesPerUnit = 2;
        vm.SaveCommand.Execute(null);

        new SettingsService(_paths).Load().MaxPassesPerUnit.Should().Be(2);
    }

    [Fact]
    public void A_cap_of_one_is_accepted_because_it_means_a_single_pass()
    {
        SettingsViewModel vm = NewViewModel();

        vm.MaxPassesPerUnit = 1;
        vm.SaveCommand.Execute(null);

        _settings.Current.MaxPassesPerUnit.Should().Be(1,
            "con tope 1 el barrido es una pasada única, que es lo que evita un selector de modo");
    }

    [Fact]
    public void A_nonsensical_cap_is_clamped_instead_of_disabling_the_audit()
    {
        SettingsViewModel vm = NewViewModel();

        vm.MaxPassesPerUnit = 0;
        vm.SaveCommand.Execute(null);

        _settings.Current.MaxPassesPerUnit.Should().Be(1);
        vm.MaxPassesPerUnit.Should().Be(1, "y la caja enseña lo que de verdad quedó guardado");
    }

    // ---------- 1c. Los mínimos se DICEN (BUGFIX-AJUSTES) ----------

    /// <summary>
    /// Un valor corregido en silencio se vive igual que un ajuste que no ajusta: escribes 0, no
    /// pasa nada, y no hay forma de saber qué número mandó. Ahora el toast lo dice, con el mínimo
    /// y el campo.
    /// </summary>
    [Theory]
    [InlineData("MaxPassesPerUnit", 0, "el tope de pasadas", 1)]
    [InlineData("PollingSeconds", 3, "la sincronización del hub", 15)]
    [InlineData("CopilotTimeoutMinutes", 0, "el timeout de Copilot", 1)]
    [InlineData("FreshnessDays", -5, "la frescura", 1)]
    public void Un_valor_por_debajo_del_minimo_se_corrige_y_se_dice(
        string property, int value, string what, int minimum)
    {
        SettingsViewModel vm = NewViewModel();
        typeof(SettingsViewModel).GetProperty(property)!.SetValue(vm, value);

        vm.SaveCommand.Execute(null);

        _toasts.Items.Should().Contain(t => t.Text.Contains(what) && t.Text.Contains($"el mínimo es {minimum}"));
        typeof(SettingsViewModel).GetProperty(property)!.GetValue(vm).Should().Be(minimum,
            "y la caja enseña lo que de verdad quedó guardado");
    }

    /// <summary>Y cuando no hay nada que corregir el aviso no inventa correcciones.</summary>
    [Fact]
    public void Sin_correcciones_el_aviso_es_el_de_siempre()
    {
        SettingsViewModel vm = NewViewModel();

        vm.SaveCommand.Execute(null);

        _toasts.Items.Should().Contain(t => t.Text == "Ajustes guardados.");
    }

    /// <summary>
    /// La frescura también refresca su caja al guardar. Antes solo lo hacían tres campos, así que
    /// un valor corregido seguía enseñando el número que el fichero no tenía.
    /// </summary>
    [Fact]
    public void La_frescura_guardada_es_la_que_queda_en_la_caja_y_en_el_fichero()
    {
        SettingsViewModel vm = NewViewModel();

        vm.FreshnessDays = 30;
        vm.SaveCommand.Execute(null);

        vm.FreshnessDays.Should().Be(30);
        new SettingsService(_paths).Load().Thresholds.FreshnessDays.Should().Be(30);
    }

    /// <summary>
    /// F13: el umbral de unidad grande YA NO SE EDITA aquí. Es política de cada aplicación porque
    /// clasifica un inventario compartido, y dos sitios editables para el mismo valor son dos
    /// verdades esperando a discrepar.
    /// </summary>
    [Fact]
    public void El_umbral_de_unidad_grande_ya_no_se_edita_en_Ajustes()
    {
        typeof(SettingsViewModel).GetProperty("LargeUnitLoc")
            .Should().BeNull("se gobierna por aplicación, en el Inventario");
        typeof(LocalThresholds).GetProperty("LargeUnitLoc")
            .Should().BeNull("y los ajustes de la máquina ya no tienen dónde guardarlo");
    }

    [Fact]
    public void Saving_does_not_reset_the_thresholds_the_page_does_not_edit()
    {
        // El umbral heredado de la máquina (F13) no tiene control en la página, y guardar no puede
        // llevárselo por delante: la mudanza todavía tiene que poder ofrecerlo.
        _settings.Current.Thresholds.LegacyLargeUnitLoc = 30;
        _settings.Save(_settings.Current);

        SettingsViewModel vm = NewViewModel();
        vm.FreshnessDays = 90;
        vm.SaveCommand.Execute(null);

        AppSettings reloaded = new SettingsService(_paths).Load();
        reloaded.Thresholds.FreshnessDays.Should().Be(90);
        reloaded.Thresholds.LegacyLargeUnitLoc.Should().Be(30,
            "construir un LocalThresholds nuevo al guardar lo devolvía a su valor por defecto");
    }

    // ---------- 1b. El guardado se ve (F5.7 §4) ----------

    /// <summary>
    /// El aviso vivía al fondo de la página: aparecía justo debajo del botón que lo provocaba
    /// pero fuera de la pantalla, así que guardar no daba ninguna señal. Ahora es un toast, que
    /// se ve sin hacer scroll y caduca solo.
    /// </summary>
    [Fact]
    public void Guardar_avisa_por_toast_y_no_por_un_texto_al_pie()
    {
        SettingsViewModel vm = NewViewModel();

        vm.SaveCommand.Execute(null);

        _toasts.Items.Should().Contain(t => t.Text.Contains("Ajustes guardados"));
    }

    // ---------- 2. Selector de modelo ----------

    [Fact]
    public async Task The_model_list_comes_from_the_sdk_with_its_price_when_it_gives_one()
    {
        var agent = new FakeCopilotAgent(modelsScript: () => new[]
        {
            new AgentModel("gpt-5", "GPT-5", 1.0),
            new AgentModel("claude-sonnet-4.5", "Claude Sonnet 4.5", 0.33),
            new AgentModel("sin-precio", "Sin precio"),
        });
        SettingsViewModel vm = NewViewModel(agent);

        await vm.LoadAsync();

        vm.Models.Select(m => m.Id).Should().Equal("gpt-5", "claude-sonnet-4.5", "sin-precio");
        // El separador decimal es el de la máquina (aquí corre en es-ES): se compara formateado.
        vm.Models.Single(m => m.Id == "claude-sonnet-4.5").Label.Should().Contain($"×{0.33:0.##}");
        vm.Models.Single(m => m.Id == "sin-precio").Label.Should().NotContain("×",
            "un multiplicador inventado sería peor que ninguno");
        vm.ModelsNotice.Should().BeEmpty();
    }

    [Fact]
    public async Task The_chosen_model_is_saved_and_survives_a_reload()
    {
        var agent = new FakeCopilotAgent(modelsScript: () => new[]
        {
            new AgentModel("gpt-5", "GPT-5", 1.0),
            new AgentModel("claude-sonnet-4.5", "Claude Sonnet 4.5", 0.33),
        });
        SettingsViewModel vm = NewViewModel(agent);
        await vm.LoadAsync();

        vm.SelectedModelId = "claude-sonnet-4.5";
        vm.SaveCommand.Execute(null);

        new SettingsService(_paths).Load().CopilotModel.Should().Be("claude-sonnet-4.5");
    }

    /// <summary>
    /// F5.15: el modelo por defecto NO es un nombre. Aquí ponía <c>"gpt-5"</c> escrito a mano y el
    /// día que GitHub lo retiró toda máquina con ajustes vírgenes nació rota. Vacío significa
    /// «pregúntaselo al runtime», y de eso se encarga <c>ModelResolver</c> al lanzar.
    /// </summary>
    [Fact]
    public void El_modelo_por_defecto_no_es_un_nombre_que_pueda_caducar()
    {
        _settings.Current.CopilotModel.Should().BeEmpty(
            "un id de modelo es un dato del proveedor con fecha de caducidad, no una constante");
        NewViewModel().SelectedModelId.Should().BeEmpty();
    }

    /// <summary>
    /// Y no queda ningún id de modelo escrito en el CÓDIGO de producción: ni como valor por
    /// defecto, ni como respaldo, ni como «preferido». La lista se pide siempre al runtime.
    /// <para>
    /// Se miran las cadenas, no los comentarios: la documentación tiene que poder contar que el
    /// literal era <c>gpt-5</c> y qué pasó el día que lo retiraron — esa es justamente la memoria
    /// que evita repetirlo. Lo que no puede volver es una cadena que el programa ejecute.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_id_de_modelo_vive_escrito_en_el_codigo_de_produccion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            // F15 — la SIEMBRA de tarifas es la excepción, y es una excepción razonada: ahí los
            // identificadores de modelo son las CLAVES de una lista de precios, no la elección de
            // con qué auditar. La diferencia es la que este guarda persigue: un modelo elegido que
            // caduca deja rota a quien instale de cero, mientras que una tarifa que caduca sale
            // como «tarifa no configurada» —está probado— y se corrige en el hub sin release.
            // La siembra además solo se escribe UNA vez y a partir de ahí manda el hub.
            if (Path.GetFileName(file) == "ModelRateSeed.cs")
            {
                continue;
            }

            string code = WithoutComments(File.ReadAllText(file));

            // Familias reales de identificadores de modelo. El punto no es esta lista concreta:
            // es que ninguna cadena con forma de id de modelo VERSIONADO viva en producción.
            //
            // F14 afina el «claude-»: `"claude-code"` es el identificador del PROVEEDOR, y ése sí
            // vive en el código a propósito —se escribe en cada sesión y en cada informe del hub,
            // así que no puede cambiar nunca—. Lo que sigue prohibido es un modelo con versión
            // dentro (`"claude-opus-5"`, `"claude-3-5-sonnet"`): eso es lo que caduca.
            foreach (string needle in new[] { "\"gpt-", "\"o3", "\"o4-", "\"gemini-" })
            {
                if (code.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(file)} contiene {needle}\"");
                }
            }

            if (Regex.IsMatch(code, "\"claude-(?!code\")", RegexOptions.IgnoreCase))
            {
                offenders.Add($"{Path.GetFileName(file)} contiene un id de modelo \"claude-…\"");
            }
        }

        offenders.Should().BeEmpty(
            "un id de modelo escrito a mano caduca sin avisar y rompe a quien instale de cero (F5.15)");
    }

    /// <summary>
    /// F14 — la excepción razonada, acotada: Claude Code se ofrece por ALIAS DE FAMILIA.
    /// <para>
    /// El CLI de <c>claude</c> no publica una lista de modelos —no hay <c>claude models list</c>,
    /// se buscó—, así que no se le puede preguntar como se le pregunta al SDK de Copilot. Lo que sí
    /// documenta su ayuda son los alias de familia, y ésos son justamente lo que NO caduca: se
    /// comprobó contra el CLI real que <c>opus</c>, <c>sonnet</c> y <c>haiku</c> resuelven a
    /// <c>claude-opus-5</c>, <c>claude-sonnet-5</c> y <c>claude-haiku-4-5-20251001</c>. El alias
    /// sobrevive a la versión; el id concreto es el que habría muerto, igual que murió
    /// <c>gpt-5</c> (F5.15).
    /// </para>
    /// <para>
    /// Este test fija las dos mitades del trato: que los alias viven en UN solo fichero —el driver,
    /// que es quien conoce a su CLI— y que el valor por defecto sigue siendo VACÍO, es decir «que
    /// elija el CLI». Una máquina recién instalada no nace con ningún modelo escrito.
    /// </para>
    /// </summary>
    [Fact]
    public void Los_alias_de_Claude_Code_viven_en_un_solo_sitio_y_el_defecto_sigue_vacio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        var carriers = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            // F15 — la SIEMBRA de tarifas es la excepción, y es una excepción razonada: ahí los
            // identificadores de modelo son las CLAVES de una lista de precios, no la elección de
            // con qué auditar. La diferencia es la que este guarda persigue: un modelo elegido que
            // caduca deja rota a quien instale de cero, mientras que una tarifa que caduca sale
            // como «tarifa no configurada» —está probado— y se corrige en el hub sin release.
            // La siembra además solo se escribe UNA vez y a partir de ahí manda el hub.
            if (Path.GetFileName(file) == "ModelRateSeed.cs")
            {
                continue;
            }

            string code = WithoutComments(File.ReadAllText(file));
            if (code.Contains("\"opus\"") || code.Contains("\"sonnet\"") || code.Contains("\"haiku\""))
            {
                carriers.Add(Path.GetFileName(file));
            }
        }

        carriers.Should().BeEquivalentTo(new[] { "ClaudeCodeProvider.cs" },
            "los alias son cosa del driver que conoce su CLI; repartidos, uno se quedaría viejo");

        new AppSettings().ClaudeCodeModel.Should().BeEmpty(
            "vacío significa «que elija el CLI»: una instalación de cero no nace con un modelo escrito");
        new AppSettings().AuditorProvider.Should().BeEmpty(
            "y sin proveedor escrito se audita con Copilot, que es como funcionaba antes de F14");
    }

    /// <summary>
    /// La contrapartida de la exención de F15: <b>la siembra de tarifas no elige modelos</b>.
    /// <para>
    /// El guarda de arriba deja pasar los identificadores de <c>ModelRateSeed</c> porque ahí son
    /// claves de precios. Este test es lo que impide que esa puerta se convierta en un atajo: si
    /// alguien usara la tabla de tarifas para poblar el selector de Ajustes, los ids volverían a
    /// ser una elección que caduca, que es justo lo que F5.15 prohibió.
    /// </para>
    /// </summary>
    [Fact]
    public void La_tabla_de_tarifas_no_es_fuente_de_modelos_seleccionables()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                // El que siembra es, por definición, el que la nombra: su trabajo es escribirla
                // en el hub una vez. Lo que se vigila es que nadie MÁS la toque.
                || Path.GetFileName(file) is "ModelRateSeed.cs" or "ModelRates.cs" or "ModelRatesService.cs")
            {
                continue;
            }

            string code = WithoutComments(File.ReadAllText(file));
            code.Should().NotContain("ModelRateSeed",
                $"{Path.GetFileName(file)} no debe sacar modelos de la tabla de tarifas; "
                + "los seleccionables los da el proveedor (F5.15)");
        }
    }

    /// <summary>El fuente sin comentarios de línea ni de bloque. Basta para lo que se vigila aquí.</summary>
    private static string WithoutComments(string source)
        => Regex.Replace(
            Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//[^\n]*", string.Empty);

    [Fact]
    public async Task When_the_list_cannot_be_fetched_settings_still_work_and_say_why()
    {
        // Con un modelo ya elegido en esta máquina: es el caso en el que hay algo que conservar.
        AppSettings configured = _settings.Current;
        configured.CopilotModel = "modelo-elegido";
        _settings.Save(configured);

        var agent = new FakeCopilotAgent(
            modelsScript: () => throw new InvalidOperationException("sin conexión con GitHub"));
        SettingsViewModel vm = NewViewModel(agent);

        await vm.LoadAsync();

        vm.ModelsNotice.Should().Contain("sin conexión con GitHub");
        vm.SelectedModelId.Should().Be("modelo-elegido", "se conserva el modelo configurado");
        vm.Models.Should().ContainSingle().Which.Id.Should().Be("modelo-elegido");

        // Y Ajustes sigue siendo usable: guardar no se rompe ni pierde el modelo.
        vm.MaxPassesPerUnit = 3;
        vm.SaveCommand.Execute(null);
        AppSettings reloaded = new SettingsService(_paths).Load();
        reloaded.MaxPassesPerUnit.Should().Be(3);
        reloaded.CopilotModel.Should().Be("modelo-elegido");
    }

    [Fact]
    public async Task A_configured_model_that_vanished_from_the_catalogue_is_kept_and_flagged()
    {
        _settings.Current.CopilotModel = "gpt-4-retirado";
        _settings.Save(_settings.Current);

        var agent = new FakeCopilotAgent(modelsScript: () => new[] { new AgentModel("gpt-5", "GPT-5", 1.0) });
        SettingsViewModel vm = NewViewModel(agent);

        await vm.LoadAsync();

        vm.SelectedModelId.Should().Be("gpt-4-retirado", "cambiar de modelo en silencio no es cosa de la app");
        vm.Models.Select(m => m.Id).Should().Contain("gpt-4-retirado");
        vm.ModelsNotice.Should().Contain("gpt-4-retirado");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
