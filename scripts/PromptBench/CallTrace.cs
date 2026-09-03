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
        public long ReasoningTokens;
        public readonly List<string> Tools = new();
    }

    /// <summary>
    /// Una muestra de consumo. <b>Abre una llamada nueva solo si el proveedor dice que lo es</b>
    /// (<c>Calls == 1</c>); lo que llega con <c>Calls == 0</c> es la MISMA llamada contada mejor y
    /// se suma a la fila que ya está abierta.
    /// <para>
    /// Desde F21 hay dos muestras por llamada: el anticipo del evento <c>assistant</c>, que trae un
    /// consumo parcial, y el <c>message_delta</c> que la cierra con el definitivo. Abriendo fila
    /// por muestra, una pasada de dos llamadas salía en la tabla con cinco, y la columna que decide
    /// —la escritura— quedaba repartida entre filas que no existen.
    /// </para>
    /// </summary>
    public void Model(UsageSample sample)
    {
        if (sample.Reconciliation)
        {
            // El cuadre del final. Va en su propia fila y NO sobre la última llamada: lo que trae
            // es, sobre todo, lo que el CLI gastó por su cuenta con su modelo auxiliar, que no es
            // de ninguna llamada del auditor. Cargárselo a la última mentiría sobre las dos.
            _calls.Add(new Call
            {
                Index = -1,
                InputTokens = sample.InputTokens,
                CacheReadTokens = sample.CacheReadTokens,
                CacheWriteTokens = sample.CacheWriteTokens,
                OutputTokens = sample.OutputTokens,
            });
            return;
        }

        if (sample.Calls <= 0 && _calls.Count > 0)
        {
            Call open = _calls[^1];
            open.InputTokens += sample.InputTokens;
            open.CacheReadTokens += sample.CacheReadTokens;
            open.CacheWriteTokens += sample.CacheWriteTokens;
            open.OutputTokens += sample.OutputTokens;
            open.ReasoningTokens += sample.ReasoningTokens;
            return;
        }

        _calls.Add(new Call
        {
            Index = _calls.Count(c => c.Index > 0) + 1,
            InputTokens = sample.InputTokens,
            CacheReadTokens = sample.CacheReadTokens,
            CacheWriteTokens = sample.CacheWriteTokens,
            OutputTokens = sample.OutputTokens,
            ReasoningTokens = sample.ReasoningTokens,
        });
    }

    /// <summary>
    /// <b>De qué está hecho lo que la vuelta siguiente reescribe en caché</b> (F21 §3). Lo que una
    /// llamada escribe es lo que se dijo desde la anterior: el mensaje entero del modelo —su
    /// razonamiento y los argumentos de sus herramientas— más los resultados que le devolvió la
    /// aplicación. La salida de la llamada anterior mide lo primero; el razonamiento, lo desglosa
    /// el propio proveedor.
    /// </summary>
    public string RenderWriteBreakdown()
    {
        var sb = new StringBuilder();
        for (int i = 1; i < _calls.Count; i++)
        {
            Call previous = _calls[i - 1];
            Call current = _calls[i];
            if (current.Index < 0 || current.CacheWriteTokens <= 0)
            {
                continue;
            }

            long resto = current.CacheWriteTokens - previous.OutputTokens;
            sb.AppendLine(
                $"    lo que ESCRIBE la llamada {current.Index} ({current.CacheWriteTokens}) ="
                + $" salida de la {previous.Index} ({previous.OutputTokens}"
                + $", de la cual razonamiento {previous.ReasoningTokens})"
                + $" + resultados de herramienta y demás ({resto})");
        }

        return sb.ToString().TrimEnd();
    }

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
    /// <summary>Tokens escritos en caché por esta pasada: el número que vigila F20.</summary>
    public long CacheWritten => _calls.Sum(c => c.CacheWriteTokens);

    /// <summary>Y los leídos, que es lo que debería crecer si la caché sirve de algo.</summary>
    public long CacheRead => _calls.Sum(c => c.CacheReadTokens);

    public string Render(string unit)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  {unit}");
        foreach (Call c in _calls)
        {
            string tools = c.Index < 0
                ? "(cuadre del final: sobre todo el modelo AUXILIAR del CLI)"
                : c.Tools.Count == 0
                    ? "(sin herramienta: solo texto)"
                    : string.Join(" + ", c.Tools);
            string label = c.Index < 0 ? "ajuste" : $"llamada {c.Index}";
            // F20 §2 — la lectura y la escritura VAN SEPARADAS. Sumadas no dicen nada: con Opus
            // escribir cuesta doce veces leer, así que dos llamadas con la misma «entrada» pueden
            // costar trece veces distinto. El síntoma que esta fase persigue —un prefijo que se
            // reescribe en vez de leerse— solo se ve en estas dos columnas.
            sb.AppendLine(
                $"    {label}: fresca {c.InputTokens} · leída {c.CacheReadTokens}"
                + $" · ESCRITA {c.CacheWriteTokens} · salida {c.OutputTokens} → {tools}");
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
