using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F23 §6 — EL ESCAPE QUE LLEGABA AL TEXTO.
/// <para>
/// En el informe de AtalayaBanco del 2026-09-03 se leía «falta de validación de argumentos». El
/// hub guarda exactamente esos doce caracteres, así que el destrozo ocurría al ENTRAR: el modelo
/// emitió su argumento con la <c>ó</c> ya escapada y nadie lo volvió a leer como JSON.
/// </para>
/// </summary>
public sealed class AuditorTextTests
{
    /// <summary>El caso exacto que se vio, con sus caracteres tal cual.</summary>
    [Fact]
    public void El_escape_que_se_colo_en_el_informe_real_se_deshace()
        => AuditorText.Clean(@"falta de validaci\u00f3n de argumentos")
            .Should().Be("falta de validación de argumentos");

    [Theory]
    [InlineData(@"a\u00f1o", "año")]
    [InlineData(@"\u00danico al principio", "Único al principio")]
    [InlineData(@"y al final: caf\u00e9", "y al final: café")]
    [InlineData(@"dos: \u00e1\u00e9", "dos: áé")]
    public void Se_deshacen_los_escapes_de_letras_acentuadas(string raw, string expected)
        => AuditorText.Clean(raw).Should().Be(expected);

    /// <summary>
    /// <b>Y no se toca nada más.</b> El texto de un hallazgo lleva CÓDIGO dentro, y ahí una barra
    /// invertida suele ser lo que el autor quería decir. Se acepta el caso raro que esto no arregla
    /// antes que estropear un fragmento de código, que es el contenido que la gente va a copiar.
    /// </summary>
    [Theory]
    [InlineData(@"un salto \n no se toca")]
    [InlineData(@"ni una tabulaci\u0009n de control")]
    [InlineData(@"ni \u0041 que es ASCII")]
    [InlineData(@"ni una barra doble \\u00f3 de un ejemplo de código")]
    [InlineData(@"ni \uZZZZ que no es hexadecimal")]
    [InlineData(@"ni un \u00 incompleto")]
    [InlineData(@"una ruta C:\usuarios\ana tampoco")]
    public void Lo_que_no_es_un_acento_escapado_se_deja_intacto(string raw)
        => AuditorText.Clean(raw).Should().Be(raw);

    [Fact]
    public void Un_texto_limpio_vuelve_tal_cual()
        => AuditorText.Clean("validación de argumentos, sin escapes")
            .Should().Be("validación de argumentos, sin escapes");

    /// <summary>Null y vacío no son asunto de esta función: no decide si un campo faltaba.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    public void Ni_null_ni_vacio_la_molestan(string? raw)
        => AuditorText.Clean(raw).Should().Be(raw);
}
