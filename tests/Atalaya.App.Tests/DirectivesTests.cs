using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F7 — directivas del proyecto. Lo que se comprueba aquí es lo que hace que la funcionalidad sea
/// lo que dice ser: que el catálogo PROPONE y nunca activa, que el registro del hub guarda rutas y
/// no contenido, que el contenido se lee del clon en el momento de usarlo, que el ámbito decide en
/// qué prompt viaja cada una, que el presupuesto se ve antes de gastarlo, y que un fichero que
/// desaparece del repo no rompe nada.
/// </summary>
public sealed class DirectivesTests : IDisposable
{
    private const string Slug = "app";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly SettingsService _settings;
    private readonly DirectiveService _directives;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public DirectivesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-directives", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug, Name = "App", RepoUrl = "https://example.invalid/app.git",
            Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _directives = new DirectiveService(_hub, new DirectiveScanner(), _ulids);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private void Write(string relativePath, string content)
    {
        string abs = Path.Combine(_clone, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private DirectivesViewModel Panel()
    {
        var vm = new DirectivesViewModel(_directives, _hub, _toasts);
        vm.Load(Slug, _clone);
        return vm;
    }

    private static DirectiveRow Row(DirectivesViewModel vm, string path)
        => vm.Rows.Single(r => r.Path == path);

    // ---------------------------------------------------------------- detección y curación

    /// <summary>
    /// El caso de aceptación de F7 §1, en su primer paso: el escaneo PROPONE y no activa nada. Que
    /// una directiva empezara a informar al auditor sin que nadie lo decidiera sería cambiar el
    /// criterio de la auditoría en silencio, que es lo contrario de para lo que existe esto.
    /// </summary>
    [Fact]
    public void Los_candidatos_detectados_nunca_se_activan_solos()
    {
        Write("AGENTS.md", "Usa records para los DTO.");
        Write("docs/adr/0001.md", "# ADR");

        DirectivesViewModel vm = Panel();

        vm.Rows.Should().HaveCount(2);
        vm.Rows.Should().OnlyContain(r => r.IsNew && !r.IsActive);
        _hub.Store.ListDirectives(Slug).Should().BeEmpty("proponer no escribe nada en el hub");
    }

    [Fact]
    public void Marcarla_la_persiste_con_su_ambito_y_quien_lo_decidio()
    {
        Write("AGENTS.md", "Usa records para los DTO.");
        DirectivesViewModel vm = Panel();

        DirectiveRow row = Row(vm, "AGENTS.md");
        row.IsActive = true;
        row.SelectedScope = DirectiveScopeNames.Display(DirectiveScope.Ambos);
        vm.ApplyScopeCommand.Execute(row);

        ProjectDirective stored = _hub.Store.ListDirectives(Slug).Single();
        stored.Path.Should().Be("AGENTS.md");
        stored.Kind.Should().Be("agents");
        stored.Scope.Should().Be(DirectiveScope.Ambos);
        stored.By.Should().NotBeEmpty();
    }

    /// <summary>
    /// Desmarcar NO borra la entrada: la deja registrada con ámbito «sin activar». Borrarla haría
    /// que el siguiente re-escaneo la volviera a anunciar como candidato nuevo, y el equipo tendría
    /// que volver a decidir lo que ya decidió.
    /// </summary>
    [Fact]
    public void Desmarcarla_la_deja_registrada_y_no_vuelve_a_anunciarse_como_nueva()
    {
        Write("AGENTS.md", "Usa records.");
        DirectivesViewModel vm = Panel();

        DirectiveRow row = Row(vm, "AGENTS.md");
        row.IsActive = false;
        vm.ApplyScopeCommand.Execute(row);

        _hub.Store.ListDirectives(Slug).Single().Scope.Should().Be(DirectiveScope.Ninguno);
        _directives.NewCandidates(Slug, _clone).Should().BeEmpty();
        Panel().Rows.Single().IsNew.Should().BeFalse();
    }

    [Fact]
    public void Un_fichero_nuevo_tras_el_re_escaneo_se_anuncia_como_candidato()
    {
        Write("AGENTS.md", "Usa records.");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Ambos);

        _directives.NewCandidates(Slug, _clone).Should().BeEmpty();

        Write("docs/adr/0002-colas.md", "# ADR 2");

        _directives.NewCandidates(Slug, _clone).Should().Equal("docs/adr/0002-colas.md");
    }

    /// <summary>La válvula de F7 §1: lo que el catálogo no conoce se añade por su ruta.</summary>
    [Fact]
    public void Se_puede_anadir_a_mano_lo_que_el_catalogo_no_conoce()
    {
        Write("documentacion/CONVENCIONES.txt", "Aquí mandamos así.");

        ProjectDirective? added = _directives.AddManual(
            Slug, _clone, "documentacion/CONVENCIONES.txt", DirectiveScope.Ambos);

        added.Should().NotBeNull();
        added!.Kind.Should().Be("manual");
        Panel().Rows.Should().ContainSingle(r => r.Path == "documentacion/CONVENCIONES.txt" && r.IsActive);
    }

    /// <summary>
    /// Una directiva añadida a mano NUNCA está entre los candidatos del catálogo —por definición:
    /// se añade porque el catálogo no conoce su ruta—, así que la existencia se comprueba en disco.
    /// Sin eso, la válvula de F7 §1 nacía con todas sus entradas marcadas «no encontrada».
    /// </summary>
    [Fact]
    public void Una_directiva_manual_que_existe_en_el_clon_no_se_marca_no_encontrada()
    {
        Write("documentacion/CONVENCIONES.txt", "Aquí mandamos así.");
        _directives.AddManual(Slug, _clone, "documentacion/CONVENCIONES.txt", DirectiveScope.Auditoria);

        DirectiveRow row = Panel().Rows.Single();

        row.Missing.Should().BeFalse();
        row.State.Should().Be("activa");
        _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria)
            .Included.Single().Text.Should().Contain("Aquí mandamos así.");
    }

    [Fact]
    public void Anadir_a_mano_una_ruta_que_no_existe_no_registra_nada()
    {
        _directives.AddManual(Slug, _clone, "no/existe.md", DirectiveScope.Ambos).Should().BeNull();
        _hub.Store.ListDirectives(Slug).Should().BeEmpty();
    }

    /// <summary>
    /// Un fichero que desaparece del repo de la app no rompe nada: la entrada se marca «no
    /// encontrada» y ni el panel ni las sesiones se caen. El repo de la app manda, y alguien
    /// decidirá si actualizar la ruta o retirar la entrada.
    /// </summary>
    [Fact]
    public void Una_directiva_que_desaparece_del_repo_se_marca_no_encontrada()
    {
        Write("AGENTS.md", "Usa records.");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Ambos);

        File.Delete(Path.Combine(_clone, "AGENTS.md"));

        DirectiveRow row = Panel().Rows.Single();
        row.Missing.Should().BeTrue();
        row.State.Should().Contain("no encontrada");
        _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria).Included.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- registro sin contenido

    /// <summary>
    /// El anti-objetivo de F7: el hub registra CUÁLES son, no lo que dicen. Si el contenido se
    /// sincronizara, sería una segunda verdad que envejece desde el día que se escribe.
    /// </summary>
    [Fact]
    public void El_hub_guarda_la_ruta_y_jamas_el_contenido()
    {
        Write("AGENTS.md", "SECRETO-DE-LAS-CONVENCIONES");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Ambos);

        string file = Directory.EnumerateFiles(_hub.HubPaths.DirectivesDir(Slug), "*.json").Single();
        string json = File.ReadAllText(file);

        json.Should().Contain("AGENTS.md");
        json.Should().NotContain("SECRETO-DE-LAS-CONVENCIONES");
    }

    /// <summary>Un fichero por directiva, como todo el hub: dos altas a la vez no colisionan.</summary>
    [Fact]
    public void Cada_directiva_es_su_propio_fichero()
    {
        Write("AGENTS.md", "a");
        Write("CLAUDE.md", "b");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Auditoria);
        _directives.SetScope(Slug, "CLAUDE.md", "claude", DirectiveScope.Arreglo);

        Directory.EnumerateFiles(_hub.HubPaths.DirectivesDir(Slug), "*.json").Should().HaveCount(2);
    }

    [Fact]
    public void Volver_a_fijar_el_ambito_de_la_misma_ruta_no_duplica_la_entrada()
    {
        Write("AGENTS.md", "a");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Auditoria);
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Arreglo);

        _hub.Store.ListDirectives(Slug).Should().ContainSingle()
            .Which.Scope.Should().Be(DirectiveScope.Arreglo);
    }

    // ---------------------------------------------------------------- ámbito y contenido vigente

    [Fact]
    public void El_ambito_decide_en_que_prompt_viaja_cada_una()
    {
        Write("solo-auditoria.md", "criterio del auditor");
        Write("solo-arreglo.md", "estilo del arreglo");
        Write("las-dos.md", "arquitectura");
        _directives.SetScope(Slug, "solo-auditoria.md", "manual", DirectiveScope.Auditoria);
        _directives.SetScope(Slug, "solo-arreglo.md", "manual", DirectiveScope.Arreglo);
        _directives.SetScope(Slug, "las-dos.md", "manual", DirectiveScope.Ambos);

        IEnumerable<string> audit = _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria)
            .Included.Select(d => d.Path);
        IEnumerable<string> fix = _directives.Bundle(Slug, _clone, DirectiveScope.Arreglo)
            .Included.Select(d => d.Path);

        audit.Should().BeEquivalentTo("solo-auditoria.md", "las-dos.md");
        fix.Should().BeEquivalentTo("solo-arreglo.md", "las-dos.md");
    }

    /// <summary>
    /// La razón de que el contenido viva en el repo de la app: la auditoría de hoy usa las
    /// convenciones de hoy, sin que nadie tenga que sincronizar nada.
    /// </summary>
    [Fact]
    public void El_contenido_se_lee_del_clon_cada_vez_y_es_siempre_el_vigente()
    {
        Write("AGENTS.md", "version-vieja");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Auditoria);

        _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria)
            .Included.Single().Text.Should().Contain("version-vieja");

        Write("AGENTS.md", "version-nueva");

        _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria)
            .Included.Single().Text.Should().Contain("version-nueva");
    }

    [Fact]
    public void Sin_clon_local_no_viaja_ninguna_directiva_y_no_revienta()
    {
        Write("AGENTS.md", "a");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Auditoria);

        _directives.Bundle(Slug, clonePath: null, DirectiveScope.Auditoria).IsEmpty.Should().BeTrue();
    }

    // ---------------------------------------------------------------- presupuesto en el panel

    [Fact]
    public void El_panel_ensena_el_consumo_de_lo_activado_por_flujo()
    {
        Write("AGENTS.md", new string('a', 4_000));      // ~1.000 tokens
        Write("CLAUDE.md", new string('b', 2_000));      // ~500 tokens
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Auditoria);
        _directives.SetScope(Slug, "CLAUDE.md", "claude", DirectiveScope.Arreglo);

        DirectivesViewModel vm = Panel();

        vm.AuditTokens.Should().BeCloseTo(1_000, 5);
        vm.FixTokens.Should().BeCloseTo(500, 5);
        vm.BudgetLabel.Should().Contain("8000").And.Contain("auditoría").And.Contain("arreglo");
    }

    [Fact]
    public void Pasarse_del_presupuesto_se_avisa_y_no_bloquea_nada()
    {
        Write("enorme.md", new string('a', 80_000));     // ~20.000 tokens
        _directives.SetScope(Slug, "enorme.md", "manual", DirectiveScope.Auditoria);

        DirectivesViewModel vm = Panel();

        vm.IsOverBudget.Should().BeTrue();
        vm.OverBudgetNotice.Should().Contain("prioridad").And.Contain("declarará");
        _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria).Included.Should().HaveCount(1);
    }

    [Fact]
    public void El_presupuesto_es_por_aplicacion_y_se_edita_desde_el_panel()
    {
        Write("AGENTS.md", "a");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Auditoria);

        DirectivesViewModel vm = Panel();
        vm.Budget = 0;
        vm.ApplyBudgetCommand.Execute(null);

        _hub.Store.TryReadApp(Slug)!.Thresholds.DirectiveTokenBudget.Should().Be(0);
        _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria).IsEmpty
            .Should().BeTrue("presupuesto 0 apaga las directivas de esta aplicación");
    }

    /// <summary>
    /// Los dos ficheros ocupan casi el presupuesto entero cada uno, así que el que entre deja un
    /// resto que ni siquiera da para un trozo legible del otro: el segundo queda omitido, no
    /// truncado. Es el caso que separa «se incluyó por prioridad» de «entró un poco de todo».
    /// </summary>
    [Fact]
    public void La_prioridad_decide_quien_entra_cuando_no_cabe_todo()
    {
        Write("primera.md", new string('a', 31_800));    // ~7.950 tokens de 8.000
        Write("segunda.md", new string('b', 31_800));
        _directives.SetScope(Slug, "primera.md", "manual", DirectiveScope.Auditoria);
        _directives.SetScope(Slug, "segunda.md", "manual", DirectiveScope.Auditoria);

        Ulid segunda = _hub.Store.ListDirectives(Slug).Single(d => d.Path == "segunda.md").Id;
        _directives.SetOrder(Slug, segunda, -1);

        DirectiveBundle bundle = _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria);

        bundle.Included.Should().ContainSingle().Which.Path.Should().Be("segunda.md");
        bundle.Omitted.Should().Equal("primera.md");
    }

    /// <summary>
    /// La vista previa lee el fichero en el momento de pedirla, no al abrir el panel: son ficheros
    /// del clon y el panel tiene que abrirse igual de rápido con tres que con trescientos.
    /// </summary>
    [Fact]
    public void La_vista_previa_ensena_el_contenido_y_su_coste()
    {
        Write("AGENTS.md", "Usa records para los DTO.");
        DirectivesViewModel vm = Panel();
        DirectiveRow row = Row(vm, "AGENTS.md");

        row.Preview.Should().BeEmpty();
        row.Cost.Should().Contain("tokens");

        vm.TogglePreviewCommand.Execute(row);

        row.IsPreviewOpen.Should().BeTrue();
        row.Preview.Should().Contain("Usa records para los DTO.");
    }

    [Fact]
    public void Sin_clon_el_panel_lo_dice_en_vez_de_ensenar_una_lista_vacia()
    {
        var vm = new DirectivesViewModel(_directives, _hub, _toasts);
        vm.Load(Slug, clonePath: null);

        vm.Scanned.Should().BeFalse();
        vm.ScanNotice.Should().Contain("No hay clon local vinculado");
    }

    // ---------------------------------------------------------------- los prompts de arreglo

    private Finding SeedFinding()
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "commit", "yo");
        return new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "criterio.arquitectura",
            Pillar = Pillar.Mejoras,
            Severity = Severity.Media,
            Title = "Título",
            Description = "Descripción",
            Impact = "Impacto",
            Recommendation = "Recomendación",
            Locations = { new Location("src/A.cs", 3) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
    }

    [Fact]
    public void El_prompt_old_school_lleva_las_convenciones_y_su_regla_de_riesgo()
    {
        Write("AGENTS.md", "usa-siempre-records");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Arreglo);

        string prompt = FixPromptBuilder.Build(
            SeedFinding(), refs: null, _directives.Bundle(Slug, _clone, DirectiveScope.Arreglo));

        prompt.Should().Contain("Convenciones del proyecto");
        prompt.Should().Contain("usa-siempre-records");
        prompt.Should().Contain("declara el conflicto como riesgo");
        prompt.Should().Contain("Preserva el contrato observable", "las reglas de F6.8 no se pierden");
    }

    [Fact]
    public void El_prompt_old_school_sin_directivas_es_el_de_siempre()
    {
        string prompt = FixPromptBuilder.Build(SeedFinding(), refs: null);

        prompt.Should().NotContain("Convenciones del proyecto");
        prompt.Should().NotContain("JERARQUÍA");
        prompt.Should().Contain("Lista al final los ficheros tocados.");
    }

    [Fact]
    public void El_prompt_de_la_sesion_interactiva_lleva_las_convenciones_y_manda_preguntar()
    {
        Write("AGENTS.md", "usa-siempre-records");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Arreglo);

        string prompt = FixSessionPrompt.Build(
            SeedFinding(), refs: null, Array.Empty<FixCodeExcerpt>(), "App",
            directives: _directives.Bundle(Slug, _clone, DirectiveScope.Arreglo));

        prompt.Should().Contain("usa-siempre-records");
        prompt.Should().Contain("Respeta las convenciones del proyecto");
        prompt.Should().Contain("ask_user");
        prompt.Should().Contain("No tienes shell", "las reglas de operación no cambian");
    }

    [Fact]
    public void El_prompt_de_la_sesion_sin_directivas_no_manda_respetar_nada()
    {
        string prompt = FixSessionPrompt.Build(
            SeedFinding(), refs: null, Array.Empty<FixCodeExcerpt>(), "App");

        prompt.Should().NotContain("Respeta las convenciones del proyecto");
        prompt.Should().NotContain("Convenciones del proyecto — tu arreglo");
    }

    // ---------------------------------------------------------------- la traza en el informe

    /// <summary>
    /// Trazabilidad de F7 §3: el informe dice CON QUÉ criterio se auditó. Sin el hash, una sesión
    /// de hace dos meses diría que hubo convenciones pero no cuáles — el contenido vive en un repo
    /// que se mueve.
    /// </summary>
    [Fact]
    public void El_informe_nombra_las_directivas_que_viajaron_con_su_hash()
    {
        Write("AGENTS.md", "usa records");
        _directives.SetScope(Slug, "AGENTS.md", "agents", DirectiveScope.Auditoria);

        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = Slug,
            Mode = AuditMode.Lotes,
            By = "yo",
            Machine = "maquina",
            StartedUtc = DateTimeOffset.UtcNow,
            Directives = _directives.Bundle(Slug, _clone, DirectiveScope.Auditoria).Records.ToList(),
        };

        string report = ReportBuilder.BuildSessionReport(
            _hub.Store.TryReadApp(Slug)!, session, Array.Empty<Finding>(),
            pendingUnits: 0, largeUnits: 0, "Org");

        report.Should().Contain("Directivas del proyecto que viajaron");
        report.Should().Contain("AGENTS.md");
        report.Should().Contain("sha256:");
    }

    [Fact]
    public void Un_informe_sin_directivas_no_ensena_la_seccion()
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = Slug,
            Mode = AuditMode.Lotes,
            By = "yo",
            Machine = "maquina",
            StartedUtc = DateTimeOffset.UtcNow,
        };

        ReportBuilder.BuildSessionReport(
                _hub.Store.TryReadApp(Slug)!, session, Array.Empty<Finding>(),
                pendingUnits: 0, largeUnits: 0, "Org")
            .Should().NotContain("Directivas del proyecto que viajaron");
    }
}
