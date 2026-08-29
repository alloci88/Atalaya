using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Atalaya.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10.2b — la barra de mandos del mapa.
/// <para>
/// El defecto reportado no era de anchos: los controles medían bien. La parte izquierda vivía en
/// una columna <c>*</c> de un <c>Grid</c> y, al no caber, la columna <b>recortaba</b> — «Atención»
/// salía cortado y ningún <c>MinWidth</c> podía evitarlo. Por eso lo que se fija aquí es que la
/// barra <b>envuelva</b>, y que cada control declare sitio para su texto más largo.
/// </para>
/// </summary>
public sealed class HeatmapBarTests
{
    private static XElement Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return XDocument
            .Load(Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", "HeatmapView.xaml"))
            .Root!;
    }

    /// <summary>La barra: el <c>Border</c> de la fila 1.</summary>
    private static XElement Bar()
        => Root().Elements().Single(e => e.Name.LocalName == "Grid")
            .Elements()
            .Single(e => e.Name.LocalName == "Border" && (string?)e.Attribute("Grid.Row") == "1");

    // ============================================ Envolver, nunca recortar

    /// <summary>
    /// <b>La barra envuelve.</b> Un <c>WrapPanel</c> pasa un grupo entero a la línea siguiente en
    /// cuanto no cabe; una columna estrella lo recorta. Un texto cortado no es una versión pequeña
    /// de la interfaz: es una interfaz rota.
    /// </summary>
    [Fact]
    public void La_barra_envuelve_en_vez_de_recortar()
    {
        XElement bar = Bar();

        bar.Elements().Should().ContainSingle()
            .Which.Name.LocalName.Should().Be("WrapPanel");

        bar.Descendants().Should().NotContain(
            e => e.Name.LocalName == "ColumnDefinition",
            "una columna estrella es lo que recortaba «Atención»");
    }

    /// <summary>
    /// Cuatro grupos —qué se mira, cómo se lee, qué se enseña y qué se saca— con un pelo entre
    /// ellos. Antes todo pesaba lo mismo y no se distinguía qué configuraba qué.
    /// </summary>
    [Fact]
    public void Los_mandos_van_agrupados_por_funcion()
    {
        var children = Bar().Elements().Single().Elements().ToList();

        children.Where(e => e.Name.LocalName == "StackPanel").Should().HaveCount(4);
        children.Where(e => e.Name.LocalName == "Border").Should().HaveCount(3, "un separador entre grupos");

        // Y van intercalados: grupo, pelo, grupo, pelo, grupo, pelo, grupo.
        children.Select(e => e.Name.LocalName)
            .Should().Equal("StackPanel", "Border", "StackPanel", "Border", "StackPanel", "Border", "StackPanel");
    }

    /// <summary>
    /// Los dos interruptores van <b>juntos y en su propio grupo</b>, no flotando entre un combo y
    /// un botón: configuran lo mismo —qué se enseña— y se leen de una vez.
    /// </summary>
    [Fact]
    public void Los_dos_interruptores_van_juntos_y_solos()
    {
        var groups = Bar().Elements().Single().Elements()
            .Where(e => e.Name.LocalName == "StackPanel")
            .ToList();

        XElement toggles = groups.Single(g => g.Elements().Any(e => e.Name.LocalName == "ToggleSwitch"));

        toggles.Elements().Should().HaveCount(2);
        toggles.Elements().Should().OnlyContain(e => e.Name.LocalName == "ToggleSwitch");

        // Y su etiqueta va DENTRO del control, no como un TextBlock suelto al lado.
        toggles.Elements().Select(e => (string?)e.Attribute("Content"))
            .Should().BeEquivalentTo(new[] { "Solo auditadas", "Ver como tabla" });
    }

    // ============================================ Los anchos, medidos

    /// <summary>
    /// <b>Cada combo declara sitio para su texto más largo.</b> No se estima: se construye el
    /// control de verdad con su opción más larga, se mide lo que pide, y se compara con el
    /// <c>MinWidth</c> escrito en el XAML.
    /// </summary>
    [Theory]
    [InlineData("{Binding MetricOptions}", "Deuda absoluta")]
    [InlineData("{Binding SortOptions}", "Cobertura")]
    public void Cada_combo_cabe_su_opcion_mas_larga(string source, string longest)
    {
        double declared = Declared(source);
        double needed = 0;

        StaRunner.Run(() =>
        {
            var box = new ComboBox();
            box.Items.Add(longest);
            box.SelectedIndex = 0;

            // Los diccionarios van en un host propio y no en Application.Current: un Application
            // tiene afinidad de hilo, y cada test corre en su hilo STA. Sin la plantilla de
            // WPF-UI el combo se mediría con la de serie y el número no valdría para nada.
            var host = new Grid();
            foreach (string pack in new[]
                     {
                         "pack://application:,,,/Wpf.Ui;component/Resources/Theme/Dark.xaml",
                         "pack://application:,,,/Wpf.Ui;component/Resources/Wpf.Ui.xaml",
                     })
            {
                host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(pack) });
            }

            host.Children.Add(box);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            needed = box.DesiredSize.Width;
        });

        needed.Should().BeGreaterThan(0, "el control se ha llegado a medir");
        declared.Should().BeGreaterThanOrEqualTo(needed,
            $"«{longest}» no cabe en los {declared} px declarados para {source}");
    }

    /// <summary>
    /// El combo de aplicación es el único que no puede «caber siempre»: su contenido es DATO —el
    /// nombre lo escribe quien da de alta la app— y puede ser tan largo como quiera. Lleva por eso
    /// suelo y techo: el suelo para que los nombres normales no lo estrechen, y el techo para que
    /// uno kilométrico no empuje la barra entera.
    /// </summary>
    [Fact]
    public void El_combo_de_aplicacion_lleva_suelo_y_techo_porque_su_texto_es_dato()
    {
        XElement box = Bar().Descendants()
            .Single(e => e.Name.LocalName == "ComboBox"
                         && (string?)e.Attribute("ItemsSource") == "{Binding AppOptions}");

        ((string?)box.Attribute("MinWidth")).Should().Be("200");
        ((string?)box.Attribute("MaxWidth")).Should().Be("320");
    }

    /// <summary>Y las opciones reales no son más largas que las que se acaban de medir.</summary>
    [Fact]
    public void Las_opciones_de_los_combos_son_las_medidas()
    {
        HeatmapViewModel vm = TestBar.ViewModel();

        vm.MetricOptions.Select(o => o.Label)
            .OrderByDescending(l => l.Length).First().Should().Be("Deuda absoluta");

        vm.SortOptions.Select(o => o.Label)
            .OrderByDescending(l => l.Length).First().Should().Be("Cobertura");

        vm.SortOptions.Select(o => o.Label).Should().Contain("Atención", "el que salía cortado");
    }

    /// <summary>El <c>MinWidth</c> que el XAML declara para el combo de ese origen.</summary>
    private static double Declared(string source)
        => double.Parse(
            (string)Bar().Descendants()
                .Single(e => e.Name.LocalName == "ComboBox" && (string?)e.Attribute("ItemsSource") == source)
                .Attribute("MinWidth")!,
            System.Globalization.CultureInfo.InvariantCulture);

}
