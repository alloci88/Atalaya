using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte C — <b>«hay cambios sin guardar» se ve, y descartar devuelve lo guardado</b>.
/// <para>
/// <b>Por qué es una regla y no forma.</b> La marca es un booleano derivado de comparar lo que hay
/// en las cajas con lo que hay en el fichero. Se rompe en silencio de las dos maneras posibles y
/// ninguna falla: si se queda encendida después de guardar, la barra miente y se acaba ignorando;
/// si no se enciende al tocar un ajuste, se cambia de página perdiendo el cambio — que es lo que
/// pasaba antes, cuando «Guardar» estaba al fondo de un scroll de dos pantallas y ni siquiera
/// estaba a la vista mientras se editaba.
/// </para>
/// <para>
/// Y hay una tercera forma de romperse que este test cubre a propósito: <b>añadir un ajuste a la
/// página y olvidarse de la comparación</b>. La huella se construye con todos los campos
/// editables; si alguien añade el noveno y no lo mete ahí, el caso de abajo lo enseña.
/// </para>
/// <para>
/// <b>Lo que NO se prueba aquí, y por qué</b> (N-5). Que la marca nazca apagada y que cambiar de
/// sección no la encienda son las dos formas de que la barra <i>sobre</i>-avise, y las dos salen
/// en la primera captura de Ajustes: si la barra dijera «hay cambios sin guardar» nada más abrir,
/// o al pulsar una sección, estaría en la foto. Lo que no sale en ninguna foto es lo de abajo —
/// tocar un ajuste y que la barra calle—, y por eso es lo que se mide.
/// </para>
/// </summary>
public sealed class SettingsSaveBarTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;

    public SettingsSaveBarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f26c-save", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
    }

    [Theory]
    [InlineData("MaxPassesPerUnit")]
    [InlineData("FreshnessDays")]
    [InlineData("PollingSeconds")]
    [InlineData("CopilotTimeoutMinutes")]
    public void Tocar_un_numero_lo_ensucia(string property)
    {
        SettingsViewModel vm = Model();
        var pi = typeof(SettingsViewModel).GetProperty(property)!;

        pi.SetValue(vm, (int)pi.GetValue(vm)! + 1);

        vm.IsDirty.Should().BeTrue($"«{property}» es un ajuste editable de la página");
    }

    [Theory]
    [InlineData("IsLightTheme")]
    [InlineData("EnableAssistedFix")]
    [InlineData("ExhaustiveSweep")]
    public void Tocar_un_interruptor_lo_ensucia(string property)
    {
        SettingsViewModel vm = Model();
        var pi = typeof(SettingsViewModel).GetProperty(property)!;

        pi.SetValue(vm, !(bool)pi.GetValue(vm)!);

        vm.IsDirty.Should().BeTrue($"«{property}» es un ajuste editable de la página");
    }

    [Fact]
    public void Guardar_apaga_la_marca()
    {
        SettingsViewModel vm = Model();
        vm.MaxPassesPerUnit = 9;
        vm.IsDirty.Should().BeTrue();

        vm.SaveCommand.Execute(null);

        vm.IsDirty.Should().BeFalse("lo guardado pasa a ser la nueva referencia");
        _settings.Current.MaxPassesPerUnit.Should().Be(9, "y guardar sigue guardando, como siempre");
    }

    /// <summary>
    /// Descartar devuelve lo que hay ESCRITO, no lo que había al abrir: es el «no era esto» de
    /// quien ha tocado un número y ya no sabe cuál era.
    /// </summary>
    [Fact]
    public void Descartar_devuelve_lo_guardado_y_apaga_la_marca()
    {
        SettingsViewModel vm = Model();
        int original = vm.FreshnessDays;
        vm.FreshnessDays = original + 30;
        vm.EnableAssistedFix = !vm.EnableAssistedFix;

        vm.DiscardCommand.Execute(null);

        vm.FreshnessDays.Should().Be(original);
        vm.EnableAssistedFix.Should().Be(_settings.Current.EnableAssistedFix);
        vm.IsDirty.Should().BeFalse();
    }

    /// <summary>Descartar no escribe: es lo que lo separa de guardar.</summary>
    [Fact]
    public void Descartar_no_toca_el_fichero()
    {
        SettingsViewModel vm = Model();
        int guardado = _settings.Current.MaxPassesPerUnit;
        vm.MaxPassesPerUnit = guardado + 5;

        vm.DiscardCommand.Execute(null);

        new SettingsService(_paths).Load().MaxPassesPerUnit.Should().Be(guardado);
    }

    private SettingsViewModel Model() => new(
        _settings,
        new Atalaya.Copilot.FakeCopilotAgent(),
        new ToastCenter(),
        new FactoryResetService(
            _hub, _paths, _settings, TestFactory.Account(_paths), new OpenSessionStore(_paths)),
        new NoReset(),
        _hub,
        new NavigationService(new NoServices()));

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
    }
}
