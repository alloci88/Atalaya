using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10.1c — el nombre de la aplicación, omitido en las bandas del mapa.
/// <para>
/// Lo que protegen estos tests no es el ahorro de seis letras: es que lo omitido sea siempre un
/// <b>hecho</b> —ese módulo lleva el nombre de su app— y no una estadística. Por eso el caso mixto
/// funciona: quien lee «Documents» entiende que ese módulo no lleva el nombre de la aplicación, no
/// que le falte algo.
/// </para>
/// </summary>
public sealed class ModulePrefixTests
{
    private static IReadOnlyDictionary<string, string?> Names(
        string app, string slug, params string[] modules)
        => ModulePrefix.DisplayNames(app, slug, modules);

    // ============================================ El caso real

    /// <summary>
    /// XBLAST, tal y como está en el hub: veintiún módulos que llevan el nombre de la aplicación y
    /// <c>Documents</c>, que no. Los veintiuno se acortan; <c>Documents</c> sale entero. Es
    /// exactamente el caso que el criterio anterior —prefijo común a todos— perdía por completo.
    /// </summary>
    [Fact]
    public void El_caso_de_xblast_acorta_los_que_llevan_el_nombre_y_deja_el_otro_entero()
    {
        var modules = new[]
        {
            "Documents", "XBLASTCommon", "XBLASTCore", "XBLASTCustomRibbonControl", "XBLASTDataBase",
            "XBLASTDensity", "XBLASTInstaller", "XBLASTInstallerBuilder", "XBLASTLocalization",
            "XBLASTLog", "XBLASTLogCaller", "XBLASTLogViewer", "XBLASTMatLab", "XBLASTOpenPit",
            "XBLASTQuickUtils", "XBLASTRecovery", "XBLASTSolver", "XBLASTTypes", "XBLASTUnderground",
            "XBLASTUpdater", "XBLASTUtils", "XBLASTVersioner",
        };

        var display = Names("XBLAST", "xblast", modules);

        display.Count(p => p.Value is not null).Should().Be(21);
        display["Documents"].Should().BeNull("no lleva el nombre de la aplicación: sale entero");
        display["XBLASTCore"].Should().Be("Core");
        display["XBLASTQuickUtils"].Should().Be("QuickUtils");
        display["XBLASTLogViewer"].Should().Be("LogViewer");
        display["XBLASTVersioner"].Should().Be("Versioner");
    }

    /// <summary>La caja da igual: la carpeta se llama como la nombró quien la creó.</summary>
    [Fact]
    public void No_se_distinguen_mayusculas_al_reconocer_el_nombre()
    {
        Names("Nomina", "nomina", "NOMINACore")["NOMINACore"].Should().Be("Core");
        Names("Nomina", "nomina", "nominaCore")["nominaCore"].Should().Be("Core");
        Names("Nomina", "nomina", "NominaCore")["NominaCore"].Should().Be("Core");
    }

    /// <summary>
    /// Se prueban el nombre y el slug, y gana el que encabece más módulos. Una app llamada «Nómina
    /// Web» con slug «nomina» tiene carpetas <c>nominaCore</c>: el nombre no casa con ninguna y el
    /// slug con todas.
    /// </summary>
    [Fact]
    public void Entre_el_nombre_y_el_slug_gana_el_que_reconoce_mas_modulos()
    {
        var display = Names("Nómina Web", "nomina", "nominaCore", "nominaUtils", "Informes");

        display["nominaCore"].Should().Be("Core");
        display["nominaUtils"].Should().Be("Utils");
        display["Informes"].Should().BeNull();
    }

    // ============================================ Las guardas

    /// <summary>Un nombre de aplicación corto no estorba: no hay nada que quitar.</summary>
    [Fact]
    public void Un_nombre_de_aplicacion_de_menos_de_tres_caracteres_no_se_omite()
    {
        Names("Ax", "ax", "AxCore", "AxUtils").Values.Should().OnlyContain(v => v == null);
        ModulePrefix.MinLength.Should().Be(3);
    }

    /// <summary>
    /// No se parte una palabra: «XBLASTern» no es un módulo llamado «ern», es un nombre que da la
    /// casualidad de empezar igual.
    /// </summary>
    [Fact]
    public void No_se_corta_a_mitad_de_palabra()
    {
        var display = Names("XBLAST", "xblast", "XBLASTern", "XBLASTCore");

        display["XBLASTern"].Should().BeNull();
        display["XBLASTCore"].Should().Be("Core", "el de al lado sí se acorta: la guarda es por módulo");
    }

    /// <summary>Y al que se quedaría sin nombre —o casi— se le deja el suyo.</summary>
    [Fact]
    public void Al_que_se_quedaria_sin_nombre_se_le_deja_entero()
    {
        var display = Names("XBLAST", "xblast", "XBLAST", "XBLASTX", "XBLASTCore");

        display["XBLAST"].Should().BeNull("se quedaría en nada");
        display["XBLASTX"].Should().BeNull("se quedaría en una letra");
        display["XBLASTCore"].Should().Be("Core");
    }

    // ============================================ La colisión

    /// <summary>
    /// <b>Dos bandas no pueden acabar rotuladas igual.</b> Acortar <c>XBLASTCore</c> dejaría dos
    /// «Core» en el mapa, y dos módulos indistinguibles son peores que un nombre largo: vuelven
    /// los DOS a su nombre entero.
    /// </summary>
    [Fact]
    public void Si_dos_modulos_quedaran_con_el_mismo_nombre_no_se_acorta_ninguno_de_los_dos()
    {
        var display = Names("XBLAST", "xblast", "XBLASTCore", "Core", "XBLASTUtils");

        display["XBLASTCore"].Should().BeNull("chocaría con el módulo «Core»");
        display["Core"].Should().BeNull();
        display["XBLASTUtils"].Should().Be("Utils", "el que no choca se queda acortado");
    }

    /// <summary>La colisión tampoco distingue mayúsculas: dos bandas casi iguales son un problema.</summary>
    [Fact]
    public void La_colision_no_distingue_mayusculas()
    {
        var display = Names("App", "app", "AppCore", "core");

        display["AppCore"].Should().BeNull();
        display["core"].Should().BeNull();
    }

    /// <summary>
    /// Y una colisión entre dos acortados. <c>XBLASTCore</c> y <c>xblastCore</c> son dos carpetas
    /// distintas en el hub —el disco distingue— y las dos darían «Core».
    /// </summary>
    [Fact]
    public void Dos_acortados_que_coinciden_vuelven_los_dos_a_su_nombre()
    {
        var display = Names("XBLAST", "xblast", "XBLASTCore", "xblastCore", "XBLASTLog");

        display["XBLASTCore"].Should().BeNull();
        display["xblastCore"].Should().BeNull();
        display["XBLASTLog"].Should().Be("Log");
    }

    // ============================================ Nada que hacer

    /// <summary>Si ningún módulo lleva el nombre de la aplicación, no se toca nada.</summary>
    [Fact]
    public void Sin_ningun_modulo_que_lleve_el_nombre_no_se_omite_nada()
    {
        var display = Names("Atalaya", "atalaya", "Dominio", "Almacen", "Interfaz");

        display.Values.Should().OnlyContain(v => v == null);
    }

    /// <summary>Sin nombre ni slug tampoco hay nada que quitar, y no se cae.</summary>
    [Fact]
    public void Sin_nombre_de_aplicacion_no_pasa_nada()
    {
        ModulePrefix.DisplayNames(null, null, new[] { "Core", "Utils" })
            .Values.Should().OnlyContain(v => v == null);

        ModulePrefix.DisplayNames(string.Empty, string.Empty, Array.Empty<string>())
            .Should().BeEmpty();
    }

    /// <summary>
    /// Con un solo módulo sí se acorta: llevar el nombre de la app es un hecho de ESE módulo, no
    /// una propiedad del conjunto. Es la diferencia con el criterio de prefijo común, que
    /// necesitaba al menos dos nombres para que la palabra «común» significara algo.
    /// </summary>
    [Fact]
    public void Con_un_solo_modulo_tambien_se_acorta()
        => Names("XBLAST", "xblast", "XBLASTCore")["XBLASTCore"].Should().Be("Core");

    /// <summary>Y acortar un nombre que no lleva el token devuelve null, no una excepción.</summary>
    [Fact]
    public void Acortar_un_nombre_que_no_lleva_el_token_no_hace_nada()
    {
        ModulePrefix.Shorten("Documents", "XBLAST").Should().BeNull();
        ModulePrefix.Shorten("XBLASTCore", "XBLAST").Should().Be("Core");
    }
}
