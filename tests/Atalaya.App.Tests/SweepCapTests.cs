using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F16 §D — EL TOPE DEL BARRIDO Y LA REGLA DE PARADA SALEN DEL MISMO PRESUPUESTO.
/// <para>
/// El tope es un PRESUPUESTO —cuánto se está dispuesto a pagar por unidad— y la convergencia es
/// otra cosa: desde D-755 el barrido necesita dos pasadas secas seguidas para darse por terminado,
/// y esas dos se pagan del mismo tope. Con 5, quedaban 3 pasadas que pudieran aportar; antes de que
/// la regla se endureciera, cuando bastaba una seca, quedaban 4. El tope no se re-ajustó entonces,
/// y con un modelo minucioso el barrido se quedaba sin margen: en el banco una unidad gastó la 5ª
/// añadiendo ubicaciones y otra llegó a la 5ª con su primera seca.
/// </para>
/// <para>
/// <b>Lo que NO se hizo, y por qué.</b> La otra opción era que una pasada que solo añade
/// ubicaciones a hallazgos ya conocidos contara como seca. Se descarta: una pasada así <b>está
/// encontrando cosas</b> —las ubicaciones son deuda real, son lo que un arreglo tiene que tocar— y
/// tratarlas como silencio pararía el barrido justo mientras el auditor enumera un defecto
/// sistémico, que es exactamente para lo que F4.1 construyó <c>add_locations</c> (D-090: un defecto
/// en N sitios es UN hallazgo con N ubicaciones). Además, «secas SEGUIDAS» dejaría de significar lo
/// que dice si una pasada productiva pudiera mantener la racha. El efecto que se buscaba —que la
/// unidad no acabe etiquetada de incompleta— se consigue con el presupuesto, que es la palanca
/// honrada: la que dice lo que cuesta.
/// </para>
/// </summary>
public sealed class SweepCapTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public SweepCapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-tope", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// La aritmética, escrita: el tope de fábrica menos las dos secas que cierran deja CUATRO
    /// pasadas que puedan aportar algo. Es la relación que había antes de D-755 y la que este
    /// número restaura.
    /// </summary>
    [Fact]
    public void El_tope_de_fabrica_deja_cuatro_pasadas_productivas()
    {
        const int drySweepsToFinish = 2;

        SettingsLimits.DefaultMaxPassesPerUnit.Should().Be(6);
        (SettingsLimits.DefaultMaxPassesPerUnit - drySweepsToFinish).Should().Be(4);
        SettingsLimits.LegacyMaxPassesPerUnit.Should().Be(5,
            "el 5 sigue nombrado porque es lo que hay escrito en las máquinas de antes de F16");
    }

    /// <summary>
    /// <b>La promoción alcanza a quien traía el valor de fábrica anterior.</b> Es el mismo caso que
    /// D-562: <c>Save</c> escribe todas las propiedades, así que en esas máquinas el 5 está escrito
    /// con todas las letras y un valor por defecto nuevo no las toca.
    /// </summary>
    [Fact]
    public void Una_maquina_con_el_tope_viejo_escrito_pasa_al_nuevo_y_se_le_dice()
    {
        var settings = new SettingsService(_paths);
        AppSettings s = settings.Load();
        s.MaxPassesPerUnit = SettingsLimits.LegacyMaxPassesPerUnit;
        settings.Save(s);

        var reopened = new SettingsService(_paths);
        reopened.Load();
        string? notice = reopened.MigrateSweepCapDefault();

        reopened.Current.MaxPassesPerUnit.Should().Be(SettingsLimits.DefaultMaxPassesPerUnit);
        notice.Should().NotBeNull("un presupuesto que sube solo y en silencio es un ajuste que no ajusta");
        notice.Should().Contain("dos pasadas secas");
        new SettingsService(_paths).Load().MaxPassesPerUnit
            .Should().Be(SettingsLimits.DefaultMaxPassesPerUnit, "y queda guardado");
    }

    /// <summary>
    /// <b>Y NO alcanza a quien eligió su número.</b> Un 3 es una decisión —gastar menos— y pisarla
    /// sería justo lo que esta promoción existe para no hacer.
    /// </summary>
    [Fact]
    public void A_quien_eligio_su_propio_tope_no_se_le_toca()
    {
        var settings = new SettingsService(_paths);
        AppSettings s = settings.Load();
        s.MaxPassesPerUnit = 3;
        settings.Save(s);

        var reopened = new SettingsService(_paths);
        reopened.Load();
        string? notice = reopened.MigrateSweepCapDefault();

        reopened.Current.MaxPassesPerUnit.Should().Be(3);
        notice.Should().BeNull("no ha cambiado nada, así que no hay nada que contar");
    }

    /// <summary>
    /// Corre UNA vez y deja constancia. Sin la marca, bajar el tope a mano no sobreviviría a cerrar
    /// la aplicación — que es otra forma de tener el ajuste roto, la contraria (D-563).
    /// </summary>
    [Fact]
    public void La_promocion_corre_una_sola_vez()
    {
        var settings = new SettingsService(_paths);
        AppSettings s = settings.Load();
        s.MaxPassesPerUnit = SettingsLimits.LegacyMaxPassesPerUnit;
        settings.Save(s);

        var first = new SettingsService(_paths);
        first.Load();
        first.MigrateSweepCapDefault().Should().NotBeNull();

        // El usuario lo baja a mano a lo que tenía.
        AppSettings mine = first.Current;
        mine.MaxPassesPerUnit = SettingsLimits.LegacyMaxPassesPerUnit;
        first.Save(mine);

        var again = new SettingsService(_paths);
        again.Load();
        again.MigrateSweepCapDefault().Should().BeNull();
        again.Current.MaxPassesPerUnit.Should().Be(SettingsLimits.LegacyMaxPassesPerUnit,
            "a partir de la promoción manda el usuario");
    }

    /// <summary>
    /// Una instalación nueva no tiene nada que promocionar: nace con el valor de fábrica y la marca
    /// puesta, así que tampoco se le cuenta nada.
    /// </summary>
    [Fact]
    public void Una_instalacion_nueva_nace_con_el_tope_nuevo_y_sin_aviso()
    {
        var settings = new SettingsService(_paths);
        settings.Load();

        settings.MigrateSweepCapDefault().Should().BeNull();
        settings.Current.MaxPassesPerUnit.Should().Be(SettingsLimits.DefaultMaxPassesPerUnit);
    }
}
