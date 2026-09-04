using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// F25 §4 (D-916) — <b>lo que <c>submit_finding</c> contesta</b>: lo de siempre más el ULID de lo
/// que acaba de crear.
/// <para>
/// En un hilo no hay pasada siguiente que vuelva a listarle la unidad entera al auditor, así que
/// ésta es la única puerta por la que aprende el identificador de lo suyo — y sin él no puede ni
/// darle veredicto ni extenderlo con <c>add_locations</c>. Estos tests fijan que no cambia nada más:
/// ni el número de tools, ni sus nombres, ni el resto de sus resultados.
/// </para>
/// </summary>
public sealed class SubmitReceiptTests
{
    /// <summary>El catálogo es el de siempre: mismas herramientas, mismos nombres, mismo orden.</summary>
    [Fact]
    public void El_catalogo_no_anade_ni_quita_ninguna_herramienta()
    {
        AuditorTools.ForAudit(new CreatingToolbox()).Select(t => t.Name)
            .Should().Equal(
                "submit_findings", "submit_finding", "report_verdicts",
                "add_locations", "unit_done", "read_signatures");
    }

    /// <summary>
    /// <b>El id vuelve, y es el del hallazgo que se acaba de crear.</b> La correspondencia es por
    /// ORDEN dentro del lote: el i-ésimo aceptado con el i-ésimo ULID nuevo.
    /// </summary>
    [Fact]
    public void El_submit_devuelve_el_ULID_de_lo_que_acaba_de_crear()
    {
        var toolbox = new CreatingToolbox();
        object? result = Call(AuditorTools.ForAudit(toolbox), "submit_findings", 2);

        string json = System.Text.Json.JsonSerializer.Serialize(result);

        json.Should().Contain(toolbox.CreatedInSweep[0]);
        json.Should().Contain(toolbox.CreatedInSweep[1]);
    }

    /// <summary>Y el singular también: es el mismo criterio, no dos.</summary>
    [Fact]
    public void El_submit_singular_tambien_lo_devuelve()
    {
        var toolbox = new CreatingToolbox();
        object? result = Call(AuditorTools.ForAudit(toolbox), "submit_finding", 1);

        System.Text.Json.JsonSerializer.Serialize(result)
            .Should().Contain(toolbox.CreatedInSweep.Single());
    }

    /// <summary>
    /// Un rechazado no consume ningún id: si los consumiera, el hallazgo siguiente recibiría el
    /// ULID de otro y el auditor daría veredicto sobre algo que no es lo suyo.
    /// </summary>
    [Fact]
    public void Un_rechazado_no_se_lleva_el_id_del_siguiente()
    {
        var toolbox = new CreatingToolbox { RejectFirst = true };
        object? result = Call(AuditorTools.ForAudit(toolbox), "submit_findings", 2);

        string json = System.Text.Json.JsonSerializer.Serialize(result);

        toolbox.CreatedInSweep.Should().HaveCount(1);
        json.Should().Contain(toolbox.CreatedInSweep[0]);
    }

    /// <summary>
    /// Y un toolbox que no sabe decir qué ha creado no se suple con nada inventado: contesta lo de
    /// siempre, sin id. Un ULID inventado sería mucho peor que ninguno.
    /// </summary>
    [Fact]
    public void Sin_quien_diga_lo_creado_no_se_inventa_ningun_id()
    {
        object? result = Call(AuditorTools.ForAudit(new SilentToolbox()), "submit_findings", 1);

        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("Id");
    }

    /// <summary>
    /// <b>Y el modelo se entera de que existe.</b> El id sin la descripción que lo nombra es un
    /// campo que nadie mira: el auditor no adivina lo que la herramienta contesta, se lo lee.
    /// </summary>
    [Fact]
    public void La_descripcion_le_dice_al_modelo_que_el_id_vuelve()
    {
        IReadOnlyList<McpTool> tools = AuditorTools.ForAudit(new CreatingToolbox());

        tools.Single(t => t.Name == "submit_findings").Description
            .Should().Contain("duplicateOf, error, id");
        tools.Single(t => t.Name == "submit_finding").Description.Should().Contain("id");
    }

    private static object? Call(IReadOnlyList<McpTool> tools, string name, int items)
    {
        string findings = string.Join(",", Enumerable.Range(1, items).Select(i =>
            $$"""
              {"ruleId":"criterio.dominio","pillar":"Fiabilidad","severity":"media",
               "title":"T{{i}}","description":"d","impact":"i","recommendation":"r",
               "symbol":"M","locations":[{"path":"A.cs","line":{{i}}}]}
              """));

        using var doc = System.Text.Json.JsonDocument.Parse($"{{\"findings\":[{findings}]}}");
        return tools.Single(t => t.Name == name).Handler(doc.RootElement);
    }

    /// <summary>Un toolbox que crea de verdad —inventa un ULID por hallazgo— y sabe decirlo.</summary>
    private sealed class CreatingToolbox : IAuditToolbox, ISweepCreations
    {
        private readonly List<string> _created = new();

        public bool RejectFirst { get; init; }

        public IReadOnlyList<string> CreatedInSweep => _created;

        public SubmitFindingResult SubmitFinding(SubmitFindingArgs args) => Create(args, first: true);

        public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
            => new(findings.Select((f, i) => Create(f, first: i == 0)).ToList());

        public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
            => new(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
            => new(true, locations.Length);

        public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
        {
        }

        public string ReadSignatures(string path) => "// firmas";

        private SubmitFindingResult Create(SubmitFindingArgs args, bool first)
        {
            if (RejectFirst && first)
            {
                return new SubmitFindingResult(false, Error: "rechazado a propósito");
            }

            _created.Add($"01JM2{_created.Count:D20}");
            return new SubmitFindingResult(true);
        }
    }

    /// <summary>Uno que persiste pero no publica lo creado: el brazo no puede inventárselo.</summary>
    private sealed class SilentToolbox : IAuditToolbox
    {
        public SubmitFindingResult SubmitFinding(SubmitFindingArgs args) => new(true);

        public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
            => new(findings.Select(_ => new SubmitFindingResult(true)).ToList());

        public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
            => new(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations) => new(true);

        public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
        {
        }

        public string ReadSignatures(string path) => "// firmas";
    }
}
