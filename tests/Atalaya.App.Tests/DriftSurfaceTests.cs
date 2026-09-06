using System.Text.RegularExpressions;
using System.Xml.Linq;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F9 §3 y §5 — lo que la deriva enseña y ofrece: indicadores ortogonales, filtros propios,
/// «Seleccionar cambiadas» y el indicador clicable del portafolio.
/// </summary>
public sealed class DriftSurfaceTests
{
    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void El_indicador_de_deriva_es_ortogonal_al_estado_de_auditoria()
    {
        // Van los DOS en la fila, uno al lado del otro: una unidad puede estar «Auditada» y
        // «Cambiada» a la vez, y sustituir uno por el otro obligaría a elegir cuál se ve.
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");

        xaml.Should().Contain("{Binding StateLabel}", "el estado de auditoría sigue estando");
        xaml.Should().Contain("{Binding DriftLabel}", "y la deriva va aparte, no en su lugar");
        xaml.Should().Contain("{Binding DriftTooltip}");
    }

    [Fact]
    public void El_color_de_la_deriva_nunca_es_el_unico_canal()
    {
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");
        var doc = XDocument.Parse(xaml);

        // El punto de color y el texto viven en el mismo panel: quien no distinga los tonos lee
        // exactamente lo mismo.
        XElement dot = doc.Descendants().Single(e =>
            e.Name.LocalName == "Ellipse"
            && e.Attribute("Fill")?.Value == "{Binding DriftInk}");

        dot.Parent!.Descendants()
            .Any(e => e.Attribute("Text")?.Value == "{Binding DriftLabel}")
            .Should().BeTrue("el texto acompaña al color en el mismo indicador");
    }

    [Fact]
    public void La_barra_ofrece_seleccionar_cambiadas_junto_a_seleccionar_pendientes()
    {
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");

        int pending = xaml.IndexOf("SelectPendingCommand", StringComparison.Ordinal);
        int changed = xaml.IndexOf("SelectChangedCommand", StringComparison.Ordinal);

        pending.Should().BeGreaterThan(0);
        changed.Should().BeGreaterThan(pending, "van juntos, y las cambiadas después de las pendientes");
    }

    [Fact]
    public void El_filtro_de_deriva_es_propio_y_tiene_sus_cuatro_opciones()
    {
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");

        xaml.Should().Contain("{Binding DriftFilter}");

        // Desde F26 §B el «Deriva:» va como ETIQUETA del control y no repetido dentro de su
        // primera opción: cada filtro dice qué filtra, y decirlo dos veces en la misma línea era
        // exactamente lo que hacía ilegible la barra («Todas · Todas · Activos · Todas»). Las
        // cuatro opciones siguen siendo cuatro, que es lo que esta regla protege.
        xaml.Should().Contain("Text=\"Deriva:\"");
        foreach (string option in new[]
                 {
                     "Todas", "Solo cambiadas",
                     "Solo arregladas sin verificar", "Solo sin historial",
                 })
        {
            xaml.Should().Contain($"<ComboBoxItem Content=\"{option}\" />");
        }
    }

    [Fact]
    public void El_panel_del_ciclo_cuenta_las_tres_lineas_por_separado()
    {
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");

        xaml.Should().Contain("{Binding ChangedUnits, Mode=OneWay}");
        xaml.Should().Contain("{Binding FixedPendingVerify, Mode=OneWay}");
        xaml.Should().Contain("{Binding NoHistoryUnits, Mode=OneWay}");
        Regex.Matches(xaml, @"Binding (ChangedUnits|FixedPendingVerify), Mode=OneWay\}").Count
            .Should().Be(2, "cada una en su línea: no se suman nunca");
    }

    [Fact]
    public void El_panel_va_en_bloques_separados_y_no_en_una_lista_corrida()
    {
        // F9.1 §2: en la lista corrida, «deriva» quedaba como una línea perdida en el medio y los
        // patrones y las directivas parecían parte de ella. Un grupo, su pelo y su título.
        // F29 §1 añade el cuarto: el coste que no se ha podido calcular, con su acción.
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");
        string panel = xaml[xaml.IndexOf("Resumen del ciclo", StringComparison.Ordinal)..];

        int ciclo = panel.IndexOf("{Binding CycleLabel}", StringComparison.Ordinal);
        int deriva = panel.IndexOf("Text=\"Deriva\"", StringComparison.Ordinal);
        int gobernanza = panel.IndexOf("Text=\"Gobernanza\"", StringComparison.Ordinal);
        int coste = panel.IndexOf("{Binding CostGapLabel}", StringComparison.Ordinal);

        ciclo.Should().BeGreaterThan(0);
        deriva.Should().BeGreaterThan(ciclo, "la deriva va después del ciclo");
        gobernanza.Should().BeGreaterThan(deriva, "y la gobernanza, después");
        coste.Should().BeGreaterThan(gobernanza, "y el coste, al final: es lo último que se mira");

        Regex.Matches(panel, "PanelDivider").Count
            .Should().Be(3, "un pelo por bloque a partir del primero: cuatro bloques, tres pelos");
        xaml.Should().Contain("x:Key=\"PanelDivider\"");
    }

    [Fact]
    public void La_rama_es_el_subtitulo_del_grupo_y_no_una_linea_suelta()
    {
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");
        string panel = xaml[xaml.IndexOf("Resumen del ciclo", StringComparison.Ordinal)..];

        int titulo = panel.IndexOf("Text=\"Deriva\"", StringComparison.Ordinal);
        int rama = panel.IndexOf("{Binding DriftBranchLabel}", StringComparison.Ordinal);
        int cambiadas = panel.IndexOf("{Binding ChangedUnits, Mode=OneWay}", StringComparison.Ordinal);

        rama.Should().BeGreaterThan(titulo, "va pegada al título del grupo");
        cambiadas.Should().BeGreaterThan(rama, "y por delante de los números que acota");

        // Desde F26 §B el peso no se escribe con un número: la rama usa `Text.Meta` —el estilo de
        // metadatos, el más pequeño del sistema— y las líneas de abajo `PanelLine`, que es el de
        // texto secundario. La regla es la misma —el subtítulo pesa menos que sus datos— dicha con
        // la escala en vez de con un 11 suelto.
        panel[rama..(rama + 200)].Should().Contain("Style=\"{StaticResource Text.Meta}\"",
            "es un subtítulo, no un dato: pesa menos que las líneas de abajo");
    }

    [Fact]
    public void Sin_deriva_el_grupo_se_colapsa_a_una_linea()
    {
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");

        xaml.Should().Contain("{Binding NoDriftLabel}");
        xaml.Should().Contain("{Binding HasNoDrift, Converter={StaticResource BoolToVisibility}}",
            "tres ceros seguidos no dicen más que una frase");
    }

    [Fact]
    public void Los_tres_bloques_comparten_jerarquia_tipografica()
    {
        // Los dos microtítulos se pintan igual: si uno pesara más que el otro, el panel volvería a
        // parecer que tiene un grupo principal y dos apéndices.
        // Desde F26 §B el tamaño no se escribe en cada rótulo: los dos usan el MISMO estilo
        // (`PanelSection`), que es una forma más fuerte de la misma regla — no pueden divergir
        // aunque alguien lo intente. Lo que se comprueba es que sigan compartiéndolo.
        string xaml = Source("src/Atalaya.App/Views/InventoryView.xaml");
        var titles = Regex.Matches(
            xaml,
            @"<TextBlock Text=""(Deriva|Gobernanza)"" Style=""\{StaticResource (?<style>[\w.]+)\}""");

        titles.Should().HaveCount(2);
        titles.Select(m => m.Groups["style"].Value).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void La_re_auditoria_no_estrena_camino_de_lanzamiento()
    {
        // Anti-objetivo declarado: «Seleccionar cambiadas» solo MARCA. Lanzar sigue siendo el
        // mismo gesto y el mismo método, con su diálogo y su estimación de coste.
        string source = Source("src/Atalaya.App/ViewModels/InventoryViewModel.cs");

        Regex.Matches(source, @"_live\.StartAsync\(").Count
            .Should().Be(1, "hay un solo sitio desde el que se lanza una sesión");
        source.Should().Contain("private void SelectChanged()");
        Regex.Matches(source, @"private void SelectChanged\(\)[\s\S]*?\n    \}").Single().Value
            .Should().NotContain("StartAsync", "seleccionar no audita: lanzar sigue siendo humano");
    }

    [Fact]
    public void La_tarjeta_del_portafolio_lleva_el_indicador_y_es_un_enlace()
    {
        string xaml = Source("src/Atalaya.App/Views/PortfolioView.xaml");

        xaml.Should().Contain("{Binding DriftLabel}");
        xaml.Should().Contain("ShowDriftCommand", "el número lleva al inventario con el filtro puesto");
        xaml.Should().Contain("{Binding DriftIsActionable}", "no enlaza a ningún sitio si no hay nada");
    }

    [Fact]
    public void Sin_clon_la_tarjeta_pide_vincular_y_no_enseña_un_cero()
    {
        var card = Card(changed: null, link: CloneLink.Unknown("app"));

        card.DriftLabel.Should().Be("Vincula tu clon para ver la deriva");
        card.DriftIsActionable.Should().BeFalse();
        card.DriftTooltip.Should().Contain("un cero aquí sería mentira");
    }

    [Fact]
    public void Con_deriva_la_tarjeta_lo_dice_en_castellano_llano()
    {
        Card(changed: 12).DriftLabel.Should().Be("12 clases cambiadas desde su auditoría");
        Card(changed: 1).DriftLabel.Should().Be("1 clase cambiada desde su auditoría");
        Card(changed: 0).DriftLabel.Should().Be("Nada ha cambiado desde su auditoría");
        Card(changed: 0, fixedPending: 2).DriftLabel.Should().Be("2 arregladas pendientes de verificar");
    }

    [Fact]
    public void El_orden_por_defecto_de_las_cambiadas_es_mas_toqueteada_primero()
    {
        var drift = new AppDrift("app", new[]
        {
            new UnitDrift("src/A.cs", DriftState.Modificada, 2),
            new UnitDrift("src/B.cs", DriftState.Modificada, 9),
            new UnitDrift("src/C.cs", DriftState.SinCambios),
            new UnitDrift("src/D.cs", DriftState.Modificada, 5),
        }, Array.Empty<OrphanFinding>());

        drift.ChangedUnits.Select(u => u.Path)
            .Should().Equal("src/B.cs", "src/D.cs", "src/A.cs");
    }

    private static AppCard Card(int? changed, int? fixedPending = null, CloneLink? link = null)
        => new(
            "app", "APP", TechStack.DotNet, 1, 10, 5, 0, 0.5,
            0, 0, 0, 0, 0, null, null, false, Array.Empty<int>())
        {
            Link = link ?? new CloneLink("app", CloneLinkState.Vinculada, @"C:\clon", null),
            ChangedUnits = changed,
            FixedPendingVerify = fixedPending,
        };
}

/// <summary>F9 §6 — el registro. Solo se guarda el dato; no se construye ninguna gráfica.</summary>
public sealed class DriftTriggerTests
{
    [Fact]
    public void Una_sesion_normal_se_registra_como_manual()
    {
        var session = new AuditSession
        {
            AppSlug = "app", By = "yo", Machine = "m", StartedUtc = DateTimeOffset.UtcNow,
        };

        session.Trigger.Should().Be(SessionTrigger.Manual,
            "es el valor de todo lo anterior a F9 y de todo lo que se elige a mano");
    }

    [Fact]
    public void El_trigger_viaja_de_la_peticion_a_la_sesion_guardada()
    {
        var request = new SessionRequest("app", AuditMode.Lotes, new[] { "src/A.cs" }, 1, SessionTrigger.Deriva);

        request.Trigger.Should().Be(SessionTrigger.Deriva);

        string source = File.ReadAllText(Path.Combine(Root(), "src", "Atalaya.App", "Services", "SessionCoordinator.cs"));
        source.Should().Contain("Trigger = request.Trigger,",
            "sin esto el dato se quedaría en la petición y no llegaría al hub");
    }

    [Fact]
    public void El_trigger_de_deriva_sale_de_seleccionar_cambiadas_y_no_de_adivinar()
    {
        string source = File.ReadAllText(Path.Combine(
            Root(), "src", "Atalaya.App", "ViewModels", "InventoryViewModel.cs"));

        source.Should().Contain("_selectionFromDrift = select;",
            "lo marca «Seleccionar cambiadas»");
        Regex.Matches(source, @"_selectionFromDrift = false;").Count
            .Should().BeGreaterThanOrEqualTo(4,
                "cualquier otro gesto sobre la selección lo apaga: una selección manual que "
                + "coincida con las cambiadas no es mantenimiento");
        source.Should().Contain(
            "SessionTrigger trigger = _selectionFromDrift ? SessionTrigger.Deriva : SessionTrigger.Manual;");
    }

    [Fact]
    public void El_trigger_sobrevive_al_viaje_por_json()
    {
        var session = new AuditSession
        {
            AppSlug = "app", By = "yo", Machine = "m",
            StartedUtc = DateTimeOffset.UtcNow, Trigger = SessionTrigger.Deriva,
        };

        string json = Storage.Json.AtalayaJson.Serialize(session);
        json.Should().Contain("\"trigger\": \"deriva\"");

        Storage.Json.AtalayaJson.Deserialize<AuditSession>(json).Trigger
            .Should().Be(SessionTrigger.Deriva);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
