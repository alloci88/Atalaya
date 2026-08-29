using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10.1b — el prefijo común de los módulos, que el mapa omite en sus bandas.
/// <para>
/// Lo que estos tests protegen no es el ahorro de seis letras: es que la omisión sea
/// <b>todo-o-nada</b>. Un mapa donde unas bandas dicen «Core» y otras «Documents» sin que se sepa
/// cuáles llevan prefijo omitido es peor que no omitir nada, porque no hay forma de deshacerlo
/// leyendo.
/// </para>
/// </summary>
public sealed class ModulePrefixTests
{
    /// <summary>El caso para el que existe: veintiuna carpetas que empiezan igual.</summary>
    [Fact]
    public void Un_prefijo_que_comparten_todos_se_detecta()
    {
        var modules = new[] { "XBLASTCore", "XBLASTCommon", "XBLASTDataBase", "XBLASTUtils" };

        string prefix = ModulePrefix.Common(modules);

        prefix.Should().Be("XBLAST");
        ModulePrefix.Elide("XBLASTCore", prefix).Should().Be("Core");
        ModulePrefix.Elide("XBLASTDataBase", prefix).Should().Be("DataBase");
    }

    /// <summary>
    /// <b>Basta uno que no lo comparta.</b> Es el caso REAL de xblast: veintiún módulos empiezan
    /// por «XBLAST» y el vigesimosegundo se llama «Documents», así que no se omite nada. Omitir en
    /// veintiuno y dejar el otro entero haría el mapa ilegible justo donde se quería aclarar.
    /// </summary>
    [Fact]
    public void Basta_un_modulo_que_no_lo_comparta_para_que_no_haya_prefijo()
    {
        var modules = new[] { "XBLASTCore", "XBLASTCommon", "XBLASTUtils", "Documents" };

        ModulePrefix.Common(modules).Should().BeEmpty();
    }

    /// <summary>
    /// El prefijo se recorta hasta donde acaba una PALABRA. El prefijo común literal de
    /// <c>XBLASTCore</c> y <c>XBLASTCommon</c> es <c>XBLASTCo</c>, y omitirlo dejaría «re» y
    /// «mmon», que no son nombres de nada.
    /// </summary>
    [Fact]
    public void El_prefijo_no_parte_una_palabra_por_la_mitad()
    {
        var modules = new[] { "XBLASTCore", "XBLASTCommon" };

        string prefix = ModulePrefix.Common(modules);

        prefix.Should().Be("XBLAST", "«XBLASTCo» dejaría «re» y «mmon»");
        modules.Select(m => ModulePrefix.Elide(m, prefix)).Should().Equal("Core", "Common");
    }

    /// <summary>Con un prefijo corto no se gana nada y se pierde contexto.</summary>
    [Fact]
    public void Un_prefijo_de_menos_de_tres_caracteres_no_se_omite()
    {
        ModulePrefix.Common(new[] { "AbCore", "AbUtils" }).Should().BeEmpty();
        ModulePrefix.MinLength.Should().Be(3);
    }

    /// <summary>
    /// Si a algún módulo no le quedara nada —o casi— después de quitarle el prefijo, no se omite en
    /// NINGUNO. O vale para todos, o no vale.
    /// </summary>
    [Fact]
    public void Si_algun_modulo_se_quedara_sin_nombre_no_se_omite_en_ninguno()
    {
        // «Core» se quedaría en cadena vacía.
        ModulePrefix.Common(new[] { "Core", "CoreUtils", "CoreDataBase" }).Should().BeEmpty();

        // Y aquí en una sola letra.
        ModulePrefix.Common(new[] { "DatosA", "DatosBCD", "DatosEFG" }).Should().BeEmpty();
    }

    /// <summary>
    /// Con un solo módulo, «el prefijo común» sería su nombre entero y la banda se quedaría en
    /// blanco. Hace falta una pareja para que la palabra «común» signifique algo.
    /// </summary>
    [Fact]
    public void Con_un_solo_modulo_no_hay_prefijo_comun()
    {
        ModulePrefix.Common(new[] { "XBLASTCore" }).Should().BeEmpty();
        ModulePrefix.Common(Array.Empty<string>()).Should().BeEmpty();
    }

    /// <summary>Sin nada en común, los nombres salen intactos.</summary>
    [Fact]
    public void Sin_nada_en_comun_no_se_toca_nada()
    {
        var modules = new[] { "Dominio", "Almacen", "Interfaz" };

        string prefix = ModulePrefix.Common(modules);

        prefix.Should().BeEmpty();
        modules.Select(m => ModulePrefix.Elide(m, prefix)).Should().Equal(modules);
    }

    /// <summary>
    /// Ordinal y sensible a mayúsculas: son nombres de carpeta. «xblastCore» y «XBLASTCore» no
    /// comparten prefijo, y decir que sí sería inventarse una equivalencia que el disco no tiene.
    /// </summary>
    [Fact]
    public void La_comparacion_es_ordinal()
        => ModulePrefix.Common(new[] { "xblastCore", "XBLASTCommon" }).Should().BeEmpty();

    /// <summary>Un nombre repetido no cambia el resultado: se compara el conjunto.</summary>
    [Fact]
    public void Los_nombres_repetidos_no_alteran_el_calculo()
        => ModulePrefix.Common(new[] { "AppCore", "AppCore", "AppUtils" }).Should().Be("App");

    /// <summary>Y quitar un prefijo que no está no hace nada.</summary>
    [Fact]
    public void Elidir_un_prefijo_que_no_esta_devuelve_el_nombre_intacto()
    {
        ModulePrefix.Elide("Documents", "XBLAST").Should().Be("Documents");
        ModulePrefix.Elide("XBLASTCore", string.Empty).Should().Be("XBLASTCore");
    }
}
