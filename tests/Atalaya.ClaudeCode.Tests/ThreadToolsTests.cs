using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// M2 — <b>el catálogo del brazo `--hilo`</b>, y lo único que le cambia respecto del de producción.
/// <para>
/// En un hilo no hay lista de existentes que reenviar, así que el auditor solo puede dar veredicto
/// sobre lo que él mismo creó si la aplicación le dice qué ULID le tocó. Eso es todo lo que cambia:
/// el resultado de los dos <c>submit</c> lleva un <c>Id</c>. Estos tests fijan que no cambia nada
/// más — ni el número de tools, ni sus nombres, ni el resto de sus resultados— y que el catálogo de
/// PRODUCCIÓN sigue sin el id.
/// </para>
/// </summary>
public sealed class ThreadToolsTests
{
    /// <summary>Producción no cambia: mismas tools, mismos nombres, mismo orden.</summary>
    [Fact]
    public void El_brazo_no_anade_ni_quita_ninguna_herramienta()
    {
        var toolbox = new CreatingToolbox();

        AuditorTools.ForThreadedAudit(toolbox).Select(t => t.Name)
            .Should().Equal(AuditorTools.ForAudit(toolbox).Select(t => t.Name));
    }

    /// <summary>
    /// <b>El id vuelve, y es el del hallazgo que se acaba de crear.</b> La correspondencia es por
    /// ORDEN dentro del lote: el i-ésimo aceptado con el i-ésimo ULID nuevo.
    /// </summary>
    [Fact]
    public void El_submit_del_brazo_devuelve_el_ULID_de_lo_que_acaba_de_crear()
    {
        var toolbox = new CreatingToolbox();
        object? result = Call(AuditorTools.ForThreadedAudit(toolbox), "submit_findings", 2);

        string json = System.Text.Json.JsonSerializer.Serialize(result);

        json.Should().Contain(toolbox.CreatedInSweep[0]);
        json.Should().Contain(toolbox.CreatedInSweep[1]);
    }

    /// <summary>
    /// Un rechazado no consume ningún id: si los consumiera, el hallazgo siguiente recibiría el
    /// ULID de otro y el auditor daría veredicto sobre algo que no es lo suyo.
    /// </summary>
    [Fact]
    public void Un_rechazado_no_se_lleva_el_id_del_siguiente()
    {
        var toolbox = new CreatingToolbox { RejectFirst = true };
        object? result = Call(AuditorTools.ForThreadedAudit(toolbox), "submit_findings", 2);

        string json = System.Text.Json.JsonSerializer.Serialize(result);

        toolbox.CreatedInSweep.Should().HaveCount(1);
        json.Should().Contain(toolbox.CreatedInSweep[0]);
    }

    /// <summary>
    /// <b>Producción sigue sin el id</b>, que es la mitad de que esto sea una medida: el brazo
    /// añade, el catálogo de siempre no se toca.
    /// </summary>
    [Fact]
    public void El_catalogo_de_produccion_no_devuelve_ningun_id()
    {
        var toolbox = new CreatingToolbox();
        object? result = Call(AuditorTools.ForAudit(toolbox), "submit_findings", 2);

        System.Text.Json.JsonSerializer.Serialize(result)
            .Should().NotContain(toolbox.CreatedInSweep[0]);
    }

    /// <summary>
    /// Y un toolbox que no sabe decir qué ha creado no se suple con nada inventado: se devuelven
    /// las tools de siempre.
    /// </summary>
    [Fact]
    public void Sin_quien_diga_lo_creado_el_brazo_devuelve_el_catalogo_de_siempre()
    {
        var mudo = new SilentToolbox();

        object? result = Call(AuditorTools.ForThreadedAudit(mudo), "submit_findings", 1);

        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("Id");
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
