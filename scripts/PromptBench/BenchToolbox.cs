using Atalaya.Agents;

namespace Atalaya.PromptBench;

/// <summary>
/// El toolbox del banco: acepta todo y cuenta. <b>No valida, no persiste y no juzga</b> — para eso
/// está el de la aplicación, y duplicar aquí su criterio sería tener dos.
/// <para>
/// Lo que sí hace es lo único que el banco necesita saber: que el agente <b>llegó a las
/// herramientas</b>. Una medición de tokens sobre una sesión en la que el modelo nunca llamó a
/// nada mediría otra cosa —un modelo confundido gasta distinto— y se leería como si fuera
/// comparable. Por eso el recuento de tools sale en la tabla junto a los tokens.
/// </para>
/// </summary>
internal sealed class BenchToolbox : IAuditToolbox
{
    private readonly string _unit;
    private readonly List<string> _titles = new();
    private int _findings;
    private int _verdicts;
    private int _locations;
    private bool _done;

    public BenchToolbox(string unit) => _unit = unit;

    /// <summary>
    /// Los títulos de lo reportado en esta pasada. Es lo que la pasada siguiente tiene que ver como
    /// «ya conocido» para que el barrido del banco se parezca al de verdad (F20 §3): sin eso, la
    /// segunda pasada re-descubriría lo mismo y la medida no sería de un barrido, sería de dos
    /// primeras pasadas.
    /// </summary>
    public IReadOnlyList<string> Titles => _titles;

    public int Findings => _findings;

    public string Describe()
        => $"{_findings} hallazgo(s), {_verdicts} veredicto(s), {_locations} ubicación(es)"
           + (_done ? ", cerrada" : ", SIN CERRAR");

    public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
    {
        _findings++;
        _titles.Add(args.Title);
        return new SubmitFindingResult(true);
    }

    public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
    {
        _findings += findings.Length;
        _titles.AddRange(findings.Select(f => f.Title));
        return new SubmitFindingsResult(findings.Select(_ => new SubmitFindingResult(true)).ToList());
    }

    public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
    {
        _verdicts += verdicts.Length;
        return new ReportVerdictsResult(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());
    }

    public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
    {
        _locations += locations.Length;
        return new AddLocationsResult(true, locations.Length);
    }

    public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
        => _done = true;

    /// <summary>El banco no lee dependencias: la medida tiene que depender solo del prompt.</summary>
    public string ReadSignatures(string path)
        => $"(el banco no sirve firmas; la unidad medida es {_unit})";
}
