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
        ICopilotAgent? agent = null, IFactoryResetConfirmer? confirmer = null)
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

    [Fact]
    public void Saving_does_not_reset_the_thresholds_the_page_does_not_edit()
    {
        _settings.Current.DefaultThresholds.MaxTokensPerUnit = 123_456;
        _settings.Current.DefaultThresholds.ClaimTtlMinutes = 45;
        _settings.Save(_settings.Current);

        SettingsViewModel vm = NewViewModel();
        vm.LargeUnitLoc = 900;
        vm.SaveCommand.Execute(null);

        AppSettings reloaded = new SettingsService(_paths).Load();
        reloaded.DefaultThresholds.LargeUnitLoc.Should().Be(900);
        reloaded.DefaultThresholds.MaxTokensPerUnit.Should().Be(123_456,
            "construir un Thresholds nuevo al guardar los devolvía a los valores por defecto");
        reloaded.DefaultThresholds.ClaimTtlMinutes.Should().Be(45);
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

    [Fact]
    public void The_default_model_is_the_one_in_use_today()
    {
        _settings.Current.CopilotModel.Should().Be("gpt-5");
        NewViewModel().SelectedModelId.Should().Be("gpt-5");
    }

    [Fact]
    public async Task When_the_list_cannot_be_fetched_settings_still_work_and_say_why()
    {
        var agent = new FakeCopilotAgent(
            modelsScript: () => throw new InvalidOperationException("sin conexión con GitHub"));
        SettingsViewModel vm = NewViewModel(agent);

        await vm.LoadAsync();

        vm.ModelsNotice.Should().Contain("sin conexión con GitHub");
        vm.SelectedModelId.Should().Be("gpt-5", "se conserva el modelo configurado");
        vm.Models.Should().ContainSingle().Which.Id.Should().Be("gpt-5");

        // Y Ajustes sigue siendo usable: guardar no se rompe ni pierde el modelo.
        vm.MaxPassesPerUnit = 3;
        vm.SaveCommand.Execute(null);
        AppSettings reloaded = new SettingsService(_paths).Load();
        reloaded.MaxPassesPerUnit.Should().Be(3);
        reloaded.CopilotModel.Should().Be("gpt-5");
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
