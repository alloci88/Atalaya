using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R2 — <b>lo que Ajustes gana y lo que Métricas conserva</b>.
/// <para>
/// Dos controles nuevos en Ajustes: el modo exhaustivo, con su icono de aviso y la frase LITERAL de
/// M2, y las tarifas, que antes se editaban en Métricas. Y Métricas se queda con lo suyo: el aviso
/// de «parcial» con su recuento (D-787) y el camino hasta el remedio.
/// </para>
/// <para>
/// Se comprueba sobre el XAML porque son reglas de disposición: no hay estado que interrogar, y lo
/// que se quiere impedir es que el precio del modo exhaustivo acabe escrito a mano en la vista con
/// otras cifras que las medidas.
/// </para>
/// </summary>
public sealed class SettingsRatesSurfaceTests
{
    // ---------- El modo exhaustivo ----------

    /// <summary>
    /// El interruptor está, enlazado al ajuste que de verdad gobierna, y con el icono de aviso al
    /// lado. Un interruptor sin el precio delante sería una invitación a triplicar la factura sin
    /// saberlo.
    /// </summary>
    [Fact]
    public void El_toggle_del_modo_exhaustivo_esta_en_ajustes_con_su_icono_de_aviso()
    {
        string xaml = Markup(Xaml("SettingsView.xaml"));

        xaml.Should().Contain("Modo exhaustivo");
        xaml.Should().Contain("{Binding ExhaustiveSweep}",
            "el control tiene que estar enlazado al ajuste, no ser decorativo");
        xaml.Should().Contain("ToolTip=\"{Binding ExhaustiveNotice}\"",
            "el aviso se enlaza, para que no haya una segunda versión del precio en el XAML");

        typeof(AppSettings).GetProperty("ExhaustiveSweep").Should().NotBeNull();
        new AppSettings().ExhaustiveSweep.Should().BeFalse("apagado de fábrica");
    }

    /// <summary>
    /// <b>El aviso, palabra por palabra.</b> Son las cifras que M2 midió (D-917, D-920): el ×3 por
    /// unidad, los duplicados y los dos defectos de gravedad media por cada veinte. Si alguna vez se
    /// vuelven a medir, este test es el que obliga a cambiarlas a la vez que DECISIONS.
    /// </summary>
    [Fact]
    public void El_aviso_del_modo_exhaustivo_es_literal()
    {
        SettingsViewModel.ExhaustiveWarning.Should().Be(
            "Aumenta el coste de forma drástica (M2: ×3 por unidad) y puede producir hallazgos "
            + "duplicados. Encuentra, de media, dos defectos de gravedad media más por cada veinte.");
    }

    /// <summary>Y guardarlo lo guarda: un interruptor que no llega al fichero es un control muerto.</summary>
    [Fact]
    public void Guardar_persiste_el_modo_exhaustivo()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-r2-set", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var settings = new SettingsService(paths);
            settings.Load();

            SettingsViewModel vm = ViewModel(paths, settings);
            vm.ExhaustiveSweep.Should().BeFalse();
            vm.ExhaustiveSweep = true;
            vm.SaveCommand.Execute(null);

            var releida = new SettingsService(paths);
            releida.Load();
            releida.Current.ExhaustiveSweep.Should().BeTrue();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------- Las tarifas ----------

    /// <summary>
    /// Las tarifas se editan en Ajustes desde R2, y el texto dice de dónde salen y cómo se corrigen
    /// — que es lo que el usuario preguntaba al no ver costes.
    /// </summary>
    [Fact]
    public void Las_tarifas_se_gestionan_desde_ajustes()
    {
        string xaml = Markup(Xaml("SettingsView.xaml"));

        xaml.Should().Contain("{Binding ManageRatesCommand}");
        xaml.Should().Contain("Tarifas");
        xaml.Should().Contain("las trae puestas y las mantiene al día sola",
            "de dónde salen, dicho donde se corrigen");

        typeof(SettingsViewModel).GetProperty("CanManageRates").Should().NotBeNull();
    }

    /// <summary>
    /// <b>Y Métricas ya no las edita</b>: conserva el aviso de parcial con su recuento —que es lo
    /// que hace accionable el hueco— y un enlace hasta Ajustes.
    /// </summary>
    [Fact]
    public void Metricas_conserva_el_aviso_de_parcial_y_enlaza_a_ajustes()
    {
        string xaml = Markup(Xaml("MetricsView.xaml"));

        xaml.Should().Contain("{Binding CostPartialNotice}", "el aviso de parcial se queda");
        xaml.Should().Contain("Ajustes → Tarifas");
        xaml.Should().NotContain("Tarifas · Gestionar", "la pantalla ya no se abre desde aquí");

        typeof(MetricsViewModel).GetProperty("CanManageRates")
            .Should().BeNull("Métricas no decide si se pueden editar: solo lleva hasta donde se editan");
    }

    /// <summary>
    /// El diálogo de tarifas se sigue registrando, y ahora lo pide Ajustes. Sin esto, mover la
    /// pantalla dejaría un botón que no abre nada — que es exactamente el control muerto de D-275.
    /// </summary>
    [Fact]
    public void Ajustes_puede_abrir_las_tarifas_cuando_se_le_dan_el_servicio_y_el_dialogo()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-r2-dlg", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var settings = new SettingsService(paths);
            settings.Load();
            HubContext hub = TestFactory.Hub(paths, settings);
            hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
            hub.SeedModelRates();

            var dialog = new SpyRatesDialog();
            SettingsViewModel vm = ViewModel(paths, settings, hub, new ModelRatesService(hub), dialog);

            vm.CanManageRates.Should().BeTrue();
            vm.ManageRatesCommand.Execute(null);

            dialog.Shown.Should().NotBeNull();
            dialog.Shown!.Rows.Should().NotBeEmpty("abre sobre la tabla sembrada");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class SpyRatesDialog : IModelRatesDialog
    {
        public ModelRatesViewModel? Shown { get; private set; }

        public ModelRatesViewModel Show(ModelRatesViewModel viewModel)
        {
            Shown = viewModel;
            return viewModel;
        }
    }

    private static SettingsViewModel ViewModel(
        AppPaths paths,
        SettingsService settings,
        HubContext? hub = null,
        ModelRatesService? rates = null,
        IModelRatesDialog? dialog = null)
    {
        HubContext context = hub ?? TestFactory.Hub(paths, settings);
        return new SettingsViewModel(
            settings,
            new Atalaya.Copilot.FakeCopilotAgent(),
            new ToastCenter(),
            new FactoryResetService(
                context, paths, settings, TestFactory.Account(paths), new OpenSessionStore(paths)),
            new NoReset(),
            context,
            new NavigationService(new EmptyServices()),
            rates: rates,
            ratesDialog: dialog);
    }

    /// <summary>El reset de fábrica no se dispara sin querer desde estos tests.</summary>
    private sealed class NoReset : IFactoryResetConfirmer
    {
        public bool Confirm(FactoryResetConfirmation confirmation) => false;
    }

    /// <summary>La navegación no se ejercita aquí; basta con que exista.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string Xaml(string view) => ViewLayout.Xaml(view);
}
