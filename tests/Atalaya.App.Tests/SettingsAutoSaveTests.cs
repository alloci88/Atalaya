using System.Reflection;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R5 — <b>cada ajuste se guarda en el momento de cambiarlo</b>.
/// <para>
/// <b>Por qué es una regla y no forma.</b> Un ajuste que deja de llegar al fichero no falla: la
/// pantalla enseña el valor nuevo, la aplicación sigue usando el viejo, y la diferencia no aparece
/// hasta la siguiente sesión — o hasta el siguiente arranque. Es exactamente el defecto que D-987
/// describió para «se cambiaba de página y se perdía lo tocado en silencio», con la diferencia de
/// que ahora no hay ningún botón que lo tape.
/// </para>
/// <para>
/// <b>Y la forma de romperse que este fichero cubre a propósito</b> es la de D-987, la misma:
/// añadir un ajuste a la página y olvidarse del sitio donde se dice que es un ajuste. Antes era la
/// huella de sucio; ahora es <see cref="SettingsViewModel.Editable"/>, y el segundo caso lo recorre
/// por reflexión para que el olvido se vea.
/// </para>
/// <para>
/// Este fichero sustituye a <c>SettingsSaveBarTests</c>, que medía la marca de sucio, «Guardar» y
/// «Descartar» — las tres cosas que R5 retira.
/// </para>
/// </summary>
public sealed class SettingsAutoSaveTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;

    public SettingsAutoSaveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-r5-autosave", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
    }

    /// <summary>
    /// Tocar un ajuste lo escribe en el fichero y enciende SU marca. Se recorre la lista entera:
    /// un ajuste que se quedara fuera del guardado lo estaría para siempre y en silencio.
    /// </summary>
    [Theory]
    [InlineData("MaxPassesPerUnit")]
    [InlineData("FreshnessDays")]
    [InlineData("PollingSeconds")]
    [InlineData("CopilotTimeoutMinutes")]
    [InlineData("IsLightTheme")]
    [InlineData("EnableAssistedFix")]
    [InlineData("ExhaustiveSweep")]
    [InlineData("Editor")]
    public void Tocar_un_ajuste_lo_guarda_al_momento_y_lo_dice_en_su_fila(string property)
    {
        SettingsViewModel vm = Model();
        PropertyInfo pi = typeof(SettingsViewModel).GetProperty(property)!;
        object? nuevo = pi.GetValue(vm) switch
        {
            int n => n + 5,
            bool b => !b,
            string t => t == "vs" ? "vscode" : "vs",
            var other => other,
        };

        pi.SetValue(vm, nuevo);

        // Sin pulsar nada: el fichero de disco ya lo tiene.
        var releido = new SettingsService(_paths);
        releido.Load();
        Value(releido.Current, property).Should().Be(pi.GetValue(vm),
            $"«{property}» se guarda al cambiarlo, no al pulsar un botón que ya no existe");
        vm.Saved[property].Shown.Should().BeTrue("y la fila lo dice con su «Guardado ✓»");
    }

    /// <summary>
    /// Ningún ajuste editable se queda fuera de la lista que dispara el guardado. Es el caso que
    /// hereda de D-987: añadir el undécimo y olvidarse de nombrarlo aquí lo dejaría sin guardar sin
    /// que nada fallara.
    /// </summary>
    [Fact]
    public void Ningun_ajuste_editable_se_queda_fuera_de_la_lista_que_los_guarda()
    {
        // Lo observable que NO es una preferencia: es el estado de la página mientras se mira.
        string[] noSonAjustes = { "Section", "ModelsNotice", "IsBusy" };

        IEnumerable<string> observables = typeof(SettingsViewModel)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.GetCustomAttributes()
                .Any(a => a.GetType().Name == "ObservablePropertyAttribute"))
            .Select(f => char.ToUpperInvariant(f.Name[1]) + f.Name[2..])
            .Except(noSonAjustes);

        observables.Should().NotBeEmpty("si la reflexión deja de encontrarlas, este caso no mide nada");
        observables.Should().BeSubsetOf(SettingsViewModel.Editable,
            "un ajuste que no está en la lista no se guarda, y no se entera nadie");
    }

    /// <summary>
    /// El mínimo se sigue aplicando y CONTANDO (BUGFIX-AJUSTES). Es lo único que queda del toast:
    /// un valor corregido en silencio se vive igual que un ajuste que no ajusta.
    /// </summary>
    [Fact]
    public void Un_minimo_aplicado_se_sigue_diciendo_por_toast()
    {
        var toasts = new ToastCenter();
        SettingsViewModel vm = Model(toasts);

        vm.PollingSeconds = 3;

        vm.PollingSeconds.Should().Be(15, "la caja enseña lo que de verdad quedó guardado");
        toasts.Items.Should().Contain(t => t.Text.Contains("la sincronización del hub"));
    }

    private SettingsViewModel Model(ToastCenter? toasts = null) => new(
        _settings,
        new Atalaya.Copilot.FakeCopilotAgent(),
        toasts ?? new ToastCenter(),
        new FactoryResetService(
            _hub, _paths, _settings, TestFactory.Account(_paths), new OpenSessionStore(_paths)),
        new NoReset(),
        _hub,
        new NavigationService(new NoServices()));

    /// <summary>Dónde vive cada ajuste dentro del fichero. Es lo único que no es uniforme.</summary>
    private static object? Value(AppSettings s, string property) => property switch
    {
        "MaxPassesPerUnit" => s.MaxPassesPerUnit,
        "FreshnessDays" => s.Thresholds.FreshnessDays,
        "PollingSeconds" => s.PollingSeconds,
        "CopilotTimeoutMinutes" => s.CopilotTimeoutMinutes,
        "IsLightTheme" => string.Equals(s.Theme, "light", StringComparison.OrdinalIgnoreCase),
        "EnableAssistedFix" => s.EnableAssistedFix,
        "ExhaustiveSweep" => s.ExhaustiveSweep,
        "Editor" => s.Editor,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "ajuste sin sitio conocido"),
    };

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
