using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F6.9 §4 — el diff del panel de cambios.
/// <para>
/// Se hizo en casa en vez de traer un paquete (ver D-541), así que tiene que estar probado como
/// una pieza propia: lo que se pinta encima del trabajo de un agente que ha escrito en el clon del
/// usuario no puede ser aproximado.
/// </para>
/// </summary>
public sealed class LineDiffTests
{
    [Fact]
    public void Un_fichero_identico_no_tiene_ni_una_linea_cambiada()
    {
        var lines = LineDiff.Compute("uno\ndos\ntres\n", "uno\ndos\ntres\n");

        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(l => l.Kind == DiffKind.Contexto);
        LineDiff.Tally(lines).Should().Be((0, 0));
    }

    [Fact]
    public void Una_linea_sustituida_sale_como_una_quitada_y_una_anadida_con_sus_numeros()
    {
        var lines = LineDiff.Compute("uno\ndos\ntres\n", "uno\nDOS\ntres\n");

        LineDiff.Tally(lines).Should().Be((1, 1));
        DiffLine removed = lines.Single(l => l.Kind == DiffKind.Quitada);
        DiffLine added = lines.Single(l => l.Kind == DiffKind.Anadida);
        removed.Text.Should().Be("dos");
        removed.OldLine.Should().Be(2);
        removed.NewLine.Should().BeNull();
        added.Text.Should().Be("DOS");
        added.NewLine.Should().Be(2);
        added.OldLine.Should().BeNull();
    }

    [Fact]
    public void Insertar_en_medio_conserva_el_contexto_de_alrededor()
    {
        var lines = LineDiff.Compute("uno\ndos\n", "uno\nuno-y-medio\ndos\n");

        LineDiff.Tally(lines).Should().Be((1, 0));
        lines.Single(l => l.Kind == DiffKind.Anadida).Text.Should().Be("uno-y-medio");
        lines.Where(l => l.Kind == DiffKind.Contexto).Select(l => l.Text).Should().Equal("uno", "dos");
    }

    /// <summary>Un fichero nuevo son todas las líneas añadidas, y ninguna quitada.</summary>
    [Fact]
    public void Un_fichero_nuevo_es_todo_anadido()
    {
        var lines = LineDiff.Compute(null, "a\nb\n");

        LineDiff.Tally(lines).Should().Be((2, 0));
    }

    /// <summary>Un fichero vacío son CERO líneas, no una en blanco.</summary>
    [Fact]
    public void Un_fichero_vacio_no_aporta_una_linea_fantasma()
        => LineDiff.Compute(string.Empty, string.Empty).Should().BeEmpty();

    /// <summary>El final de línea del sistema no es un cambio: el contenido es el mismo.</summary>
    [Fact]
    public void Los_finales_de_linea_no_cuentan_como_cambio()
        => LineDiff.Tally(LineDiff.Compute("a\r\nb\r\n", "a\nb\n")).Should().Be((0, 0));

    /// <summary>
    /// Se quita UN salto final —el de cierre de fichero—, no todos: un fichero que termina en tres
    /// líneas en blanco y otro que termina en una no son el mismo fichero.
    /// </summary>
    [Fact]
    public void Las_lineas_en_blanco_del_final_si_cuentan()
        => LineDiff.Tally(LineDiff.Compute("a\n", "a\n\n\n")).Should().Be((2, 0));

    /// <summary>
    /// El plegado deja tres líneas de contexto a cada lado y dice cuántas se ha saltado. Un diff
    /// que oculta que oculta algo es peor que uno largo.
    /// </summary>
    [Fact]
    public void El_plegado_deja_contexto_y_declara_lo_que_se_salta()
    {
        string before = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"linea {i}"));
        string after = before.Replace("linea 20", "LINEA 20");

        var collapsed = LineDiff.Collapsed(before, after);

        collapsed.Should().Contain(l => l.Kind == DiffKind.Anadida && l.Text == "LINEA 20");
        collapsed.Count(l => l.Kind == DiffKind.Contexto).Should().Be(6, "tres a cada lado");
        collapsed.Should().Contain(l => l.Kind == DiffKind.Salto && l.Text.Contains("sin cambios"));
        collapsed.Count.Should().BeLessThan(40, "para eso se pliega");
    }

    /// <summary>
    /// Un cambio pequeño en un fichero enorme se resuelve por el recorte de prefijo y sufijo: sin
    /// eso, la tabla de LCS de 5.000 × 5.000 sería lo que hace que el panel se quede pensando.
    /// </summary>
    [Fact]
    public void Un_cambio_pequeno_en_un_fichero_enorme_se_resuelve_igual()
    {
        string before = string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"linea {i}"));
        string after = before.Replace("linea 3000", "LINEA 3000");

        var lines = LineDiff.Compute(before, after);

        LineDiff.Tally(lines).Should().Be((1, 1));
        lines.Should().NotContain(l => l.Kind == DiffKind.Salto);
    }

    /// <summary>
    /// Y cuando el trozo del medio es de verdad enorme, se DICE y se enseña como reemplazo entero
    /// en vez de inventarse correspondencias línea a línea.
    /// </summary>
    [Fact]
    public void Un_cambio_gigantesco_se_declara_en_vez_de_fingir_un_diff_fino()
    {
        string before = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"a {i}"));
        string after = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"b {i}"));

        var lines = LineDiff.Compute(before, after);

        lines.Should().Contain(l => l.Kind == DiffKind.Salto && l.Text.Contains("demasiado grande"));
        LineDiff.Tally(lines).Should().Be((3000, 3000));
    }

    /// <summary>El marcador va delante SIEMPRE: el color solo refuerza lo que ya se lee.</summary>
    [Theory]
    [InlineData(DiffKind.Anadida, "+")]
    [InlineData(DiffKind.Quitada, "−")]
    [InlineData(DiffKind.Contexto, " ")]
    [InlineData(DiffKind.Salto, "⋯")]
    public void Cada_clase_de_linea_lleva_su_marcador(DiffKind kind, string marker)
        => new DiffLine(kind, null, null, string.Empty).Marker.Should().Be(marker);
}
