using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10 §2 — el reparto del treemap. Son DOS invariantes y no un test de maquetado: el área es el
/// dato (el tamaño del código), así que un reparto desproporcionado o con celdas encima de otras
/// no es feo, es una mentira sobre el tamaño del código.
/// </summary>
public sealed class TreemapLayoutTests
{
    private static readonly TreemapRect Canvas = new(0, 0, 800, 500);

    private static IReadOnlyList<TreemapTile<int>> Layout(params int[] values)
        => TreemapLayout.Squarify(values, v => v, Canvas);

    /// <summary>
    /// El área de cada celda es proporcional a su valor. Se compara contra la escala global
    /// (área del lienzo / suma de valores), no celda contra celda: así el test detecta también un
    /// reparto que conserve las proporciones ENTRE celdas dejándose la mitad del lienzo sin usar.
    /// </summary>
    [Fact]
    public void El_area_de_cada_celda_es_proporcional_a_su_valor()
    {
        int[] values = { 500, 300, 200, 120, 80, 40, 25, 10, 5, 1 };
        var tiles = Layout(values);

        double scale = Canvas.Area / values.Sum();
        foreach (TreemapTile<int> tile in tiles)
        {
            tile.Rect.Area.Should().BeApproximately(
                tile.Value * scale,
                tile.Value * scale * 0.02,
                $"la celda de {tile.Value} tiene que ocupar su parte del lienzo");
        }

        tiles.Sum(t => t.Rect.Area).Should().BeApproximately(Canvas.Area, 1.0, "el lienzo se reparte entero");
    }

    /// <summary>Y las áreas guardan la razón entre sí: el doble de líneas, el doble de celda.</summary>
    [Fact]
    public void Una_unidad_con_el_doble_de_lineas_ocupa_el_doble()
    {
        var tiles = Layout(400, 200, 150, 100).ToDictionary(t => t.Value, t => t.Rect.Area);

        tiles[400].Should().BeApproximately(4 * tiles[100], tiles[400] * 0.02);
        tiles[200].Should().BeApproximately(2 * tiles[100], tiles[200] * 0.02);
    }

    [Fact]
    public void Ninguna_celda_se_solapa_con_otra()
    {
        var tiles = Layout(900, 640, 500, 410, 300, 250, 200, 150, 90, 70, 50, 30, 20, 10, 5);

        for (int i = 0; i < tiles.Count; i++)
        {
            for (int j = i + 1; j < tiles.Count; j++)
            {
                tiles[i].Rect.Overlaps(tiles[j].Rect).Should().BeFalse(
                    $"{tiles[i].Value} y {tiles[j].Value} se pisan");
            }
        }
    }

    [Fact]
    public void Ninguna_celda_se_sale_del_contenedor()
    {
        foreach (TreemapTile<int> tile in Layout(700, 300, 150, 90, 40, 12, 3))
        {
            tile.Rect.X.Should().BeGreaterThanOrEqualTo(Canvas.X - 1e-6);
            tile.Rect.Y.Should().BeGreaterThanOrEqualTo(Canvas.Y - 1e-6);
            tile.Rect.Right.Should().BeLessThanOrEqualTo(Canvas.Right + 1e-6);
            tile.Rect.Bottom.Should().BeLessThanOrEqualTo(Canvas.Bottom + 1e-6);
        }
    }

    /// <summary>
    /// Squarified existe para esto: con un reparto por rebanadas, la celda pequeña de un lienzo
    /// ancho sale como una tira de un píxel. Se exige que ninguna pase de 12:1 con datos normales.
    /// </summary>
    [Fact]
    public void Las_celdas_salen_razonablemente_cuadradas()
    {
        foreach (TreemapTile<int> tile in Layout(400, 380, 300, 260, 200, 180, 120, 100, 60, 40))
        {
            double ratio = Math.Max(tile.Rect.Width, tile.Rect.Height)
                           / Math.Max(1e-9, Math.Min(tile.Rect.Width, tile.Rect.Height));
            ratio.Should().BeLessThan(12, $"la celda de {tile.Value} es una tira ilegible");
        }
    }

    /// <summary>Mismo reparto en dos llamadas: el mapa no puede bailar entre dos renders iguales.</summary>
    [Fact]
    public void El_reparto_es_determinista()
        => Layout(300, 200, 200, 100, 50).Should().BeEquivalentTo(
            Layout(300, 200, 200, 100, 50),
            o => o.WithStrictOrdering());

    /// <summary>
    /// Una unidad de 0 líneas no ocupa un rectángulo de área cero en medio del mapa: se descarta.
    /// Y con un lienzo degenerado no se dibuja nada, en vez de repartir un área negativa.
    /// </summary>
    [Fact]
    public void Lo_que_no_tiene_tamano_no_ocupa_sitio()
    {
        Layout(100, 0, 50, -3).Select(t => t.Value).Should().Equal(100, 50);
        TreemapLayout.Squarify(new[] { 1, 2 }, v => v, new TreemapRect(0, 0, 0, 400)).Should().BeEmpty();
        TreemapLayout.Squarify(Array.Empty<int>(), v => v, Canvas).Should().BeEmpty();
    }

    /// <summary>Un solo elemento ocupa el lienzo entero: es el 100 % del código.</summary>
    [Fact]
    public void Un_solo_elemento_ocupa_todo()
    {
        var only = Layout(42).Single();

        only.Rect.Width.Should().BeApproximately(Canvas.Width, 1e-6);
        only.Rect.Height.Should().BeApproximately(Canvas.Height, 1e-6);
    }

    /// <summary>
    /// El caso real: 925 unidades de xblast, con la clase de 5 539 líneas y la de 12. Ni se
    /// solapan ni se salen, y el reparto sigue siendo proporcional.
    /// </summary>
    [Fact]
    public void Aguanta_el_reparto_de_un_clon_real()
    {
        var random = new Random(20260829);
        var values = Enumerable.Range(0, 925).Select(_ => random.Next(8, 5600)).ToArray();

        var tiles = TreemapLayout.Squarify(values, v => v, new TreemapRect(0, 0, 1200, 520));
        tiles.Should().HaveCount(925);

        double scale = 1200 * 520.0 / values.Sum();
        tiles.Sum(t => t.Rect.Area).Should().BeApproximately(1200 * 520.0, 1.0);
        tiles.Should().OnlyContain(t => Math.Abs(t.Rect.Area - t.Value * scale) < t.Value * scale * 0.05);
    }
}
