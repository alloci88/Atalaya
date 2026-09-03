using System.Text;
using Atalaya.Agents;

namespace Atalaya.PromptBench;

/// <summary>
/// <b>Qué motivó cada llamada al modelo</b> (F19 §1). El diagnóstico que faltaba: se sabía cuántas
/// llamadas hacía falta por unidad —once en la línea base— y no en qué se iban.
/// <para>
/// La atribución no necesita nada del proveedor: una llamada al modelo termina de una de dos
/// formas, pidiendo una herramienta o cerrando el turno, y las herramientas pasan todas por el
/// toolbox. Basta con intercalar las dos series en el orden en que ocurren: cada muestra de
/// consumo abre una llamada, y las tools que lleguen después son lo que esa llamada pidió. Una
/// llamada sin tools detrás es texto — y en una auditoría, texto es una llamada tirada: los
/// hallazgos viajan por herramienta, nunca por prosa.
/// </para>
/// </summary>
internal sealed class CallTrace
{
    private readonly List<Call> _calls = new();

    private sealed class Call
    {
        public int Index;
        public long InputTokens;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long OutputTokens;
        public readonly List<string> Tools = new();
    }

    /// <summary>Una llamada al modelo, según la informa el proveedor.</summary>
    public void Model(UsageSample sample)
        => _calls.Add(new Call
        {
            Index = _calls.Count + 1,
            InputTokens = sample.InputTokens,
            CacheReadTokens = sample.CacheReadTokens,
            CacheWriteTokens = sample.CacheWriteTokens,
            OutputTokens = sample.OutputTokens,
        });

    /// <summary>
    /// Una herramienta, atribuida a la llamada en curso. Si llegara antes de la primera muestra de
    /// consumo se apunta igual, en una llamada 0: perder una tool sería perder justo el motivo.
    /// </summary>
    public void Tool(string name)
    {
        if (_calls.Count == 0)
        {
            _calls.Add(new Call { Index = 0 });
        }

        _calls[^1].Tools.Add(name);
    }

    public int Calls => _calls.Count(c => c.Index > 0);

    /// <summary>Llamadas que no pidieron ninguna herramienta: en una auditoría, no aportan nada.</summary>
    public int CallsWithoutTools => _calls.Count(c => c.Index > 0 && c.Tools.Count == 0);

    /// <summary>El mapa, una línea por llamada.</summary>
    public string Render(string unit)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  {unit}");
        foreach (Call c in _calls)
        {
            string tools = c.Tools.Count == 0
                ? "(sin herramienta: solo texto)"
                : string.Join(" + ", c.Tools);
            sb.AppendLine(
                $"    llamada {c.Index}: entrada {c.InputTokens + c.CacheReadTokens + c.CacheWriteTokens}"
                + $" · salida {c.OutputTokens} → {tools}");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// El toolbox del banco, además, <b>apunta a qué llamada pertenece cada herramienta</b> (F19 §1).
/// </summary>
internal sealed class TracingToolbox : IAuditToolbox
{
    private readonly BenchToolbox _inner;
    private readonly CallTrace _trace;

    public TracingToolbox(BenchToolbox inner, CallTrace trace)
    {
        _inner = inner;
        _trace = trace;
    }

    public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
    {
        _trace.Tool("submit_finding (singular)");
        return _inner.SubmitFinding(args);
    }

    public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
    {
        _trace.Tool($"submit_findings × {findings.Length}");
        return _inner.SubmitFindings(findings);
    }

    public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
    {
        _trace.Tool($"report_verdicts × {verdicts.Length}");
        return _inner.ReportVerdicts(verdicts);
    }

    public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
    {
        _trace.Tool($"add_locations × {locations.Length}");
        return _inner.AddLocations(findingId, locations);
    }

    public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
    {
        _trace.Tool("unit_done");
        _inner.UnitDone(unitPath, summary, suppressedByPattern);
    }

    public string ReadSignatures(string path)
    {
        _trace.Tool($"read_signatures({path})");
        return _inner.ReadSignatures(path);
    }
}
