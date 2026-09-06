using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// F21 §1 — LAS CUENTAS DE CADA LLAMADA, ANTES DEL EVENTO FINAL.
/// <para>
/// F19 dio por imposible cortar la pasada porque «el CLI solo publica el consumo real en su evento
/// final» (D-865). Era cierto de los eventos que se estaban leyendo: el <c>usage</c> de un evento
/// <c>assistant</c> es <b>parcial</b> —medido contra el CLI real: decía <c>output_tokens: 5</c>
/// donde el <c>result</c> de esa misma llamada decía 20—.
/// </para>
/// <para>
/// Lo que faltaba era pedirlos. Con <c>--include-partial-messages</c> el CLI reenvía los eventos
/// CRUDOS de la API, y el <c>message_delta</c> que cierra un mensaje trae el consumo
/// <b>definitivo</b> de esa llamada, en los cuatro conceptos. Conciliación medida sobre el CLI real
/// (2.1.259): la suma de los <c>message_delta</c> coincide <b>al token</b> con el
/// <c>modelUsage</c> del modelo principal — diferencias 0 en fresca, leída, escrita y salida.
/// </para>
/// <para>
/// Lo que esos eventos <b>no</b> traen —y por eso el corte no se hace matando el proceso— es el
/// modelo AUXILIAR que el CLI usa por su cuenta. Ése solo aparece en el <c>modelUsage</c> del
/// evento final, y por eso el corte es una interrupción y no una muerte: así ese evento llega.
/// </para>
/// </summary>
public sealed class PerCallAccountingTests
{
    /// <summary>
    /// El anticipo del evento <c>assistant</c> mueve el pie; el <c>message_delta</c> lo corrige a
    /// la cifra buena. Lo que se emite es <b>la diferencia</b>, así que quien suma las muestras
    /// tiene el consumo real de la llamada y no el parcial ni el doble.
    /// </summary>
    [Fact]
    public async Task El_consumo_definitivo_de_una_llamada_llega_al_cerrarse_su_mensaje()
    {
        var samples = new List<UsageSample>();

        await Read(
            samples,
            Init(),
            Start("msg_1", input: 2, cacheRead: 11_322, cacheWrite: 13_681),
            Assistant("msg_1", input: 2, output: 5),
            Delta(input: 2, output: 4_087, cacheRead: 11_322, cacheWrite: 13_681));

        samples.Sum(s => s.OutputTokens).Should().Be(4_087, "manda el definitivo, no el anticipo de 5");
        samples.Sum(s => s.CacheWriteTokens).Should().Be(13_681);
        samples.Sum(s => s.Calls).Should().Be(1, "una llamada es una llamada, hagan falta dos eventos o tres");
    }

    /// <summary>
    /// La condición que gobierna el corte. <b>Mientras haya una petición en vuelo no se corta</b>,
    /// y no por prudencia: medido contra el CLI real, interrumpir a mitad de respuesta hace que el
    /// modelo principal desaparezca ENTERO del <c>modelUsage</c> del evento final. Esa llamada se
    /// habría pagado sin quedar registrada en ningún sitio.
    /// </summary>
    [Fact]
    public async Task Con_una_peticion_en_vuelo_las_cuentas_NO_estan()
    {
        var reader = new ClaudeStreamReader();
        var pipe = new BlockingReader(Init(), Start("msg_1", 2, 11_322, 13_681));

        Task<ClaudeRunOutcome> reading = reader.ReadAsync(pipe, CancellationToken.None);
        await pipe.Drained;

        reader.AccountingIsComplete.Should().BeFalse("hay un mensaje abierto y sin cerrar");

        pipe.Push(Delta(input: 2, output: 40, cacheRead: 11_322, cacheWrite: 13_681));
        await pipe.Drained;

        reader.AccountingIsComplete.Should().BeTrue("el mensaje cerró y trajo su consumo final");
        reader.SettledCalls.Should().Be(1);

        pipe.Close();
        await reading;
    }

    /// <summary>
    /// Y sin los eventos crudos <b>nunca</b> están: un CLI que no los publique —o una invocación
    /// que no los pida— no puede cortar. Es el mismo blindaje por el otro lado: sin cuentas por
    /// llamada, se paga la vuelta y se dice, en vez de ahorrar a ciegas.
    /// </summary>
    [Fact]
    public async Task Sin_eventos_crudos_no_hay_cuentas_por_llamada_y_no_se_corta()
    {
        var reader = new ClaudeStreamReader();

        await reader.ReadAsync(
            new StringReader(string.Join('\n', Init(), Assistant("msg_1", input: 10, output: 4))),
            CancellationToken.None);

        reader.AccountingIsComplete.Should().BeFalse();
        reader.SettledCalls.Should().Be(0);
    }

    /// <summary>
    /// El cuadre del final se marca como lo que es. Desde F21 hay dos muestras por llamada y las
    /// tres llevan <c>Calls = 0</c>; sin distinguirlo, un desglose por llamada le carga a la última
    /// todo lo que el CLI gastó por su cuenta con su modelo auxiliar, que no es de ninguna llamada
    /// del auditor.
    /// </summary>
    [Fact]
    public async Task El_cuadre_del_final_va_marcado_y_no_es_una_llamada()
    {
        var samples = new List<UsageSample>();

        await Read(
            samples,
            Init(),
            Start("msg_1", input: 2, cacheRead: 100, cacheWrite: 200),
            Assistant("msg_1", input: 2, output: 5),
            Delta(input: 2, output: 40, cacheRead: 100, cacheWrite: 200),
            Result(modelInput: 10_822, modelOutput: 52, cacheRead: 100, cacheWrite: 200));

        UsageSample adjustment = samples.Should().ContainSingle(s => s.Reconciliation).Subject;
        adjustment.InputTokens.Should().Be(10_820, "lo que el CLI gastó con su modelo auxiliar");
        adjustment.Calls.Should().Be(0);

        samples.Where(s => !s.Reconciliation).Sum(s => s.InputTokens)
            .Should().Be(2, "la llamada del auditor gastó 2 de entrada fresca, no 10.822");
    }

    /// <summary>
    /// <b>La conciliación, que es la exigencia entera de §1</b>: sumar lo que se fue emitiendo
    /// llamada a llamada tiene que dar exactamente lo que el CLI declara al final, en los cuatro
    /// conceptos. Al token, porque es lo que se va a declarar como consumido.
    /// </summary>
    [Fact]
    public async Task La_suma_por_llamada_cuadra_AL_TOKEN_con_el_agregado_del_CLI()
    {
        var samples = new List<UsageSample>();

        ClaudeRunOutcome outcome = await Read(
            samples,
            Init(),
            Start("msg_1", input: 2, cacheRead: 11_322, cacheWrite: 12_753),
            Assistant("msg_1", input: 2, output: 7),
            Delta(input: 2, output: 6_835, cacheRead: 11_322, cacheWrite: 12_753),
            Start("msg_2", input: 10_059, cacheRead: 24_075, cacheWrite: 7_054),
            Assistant("msg_2", input: 10_059, output: 9),
            Delta(input: 10_059, output: 15_515, cacheRead: 24_075, cacheWrite: 7_054),
            Result(
                modelInput: 10_061, modelOutput: 22_350,
                cacheRead: 35_397, cacheWrite: 19_807));

        samples.Sum(s => s.InputTokens).Should().Be(10_061);
        samples.Sum(s => s.OutputTokens).Should().Be(22_350);
        samples.Sum(s => s.CacheReadTokens).Should().Be(35_397);
        samples.Sum(s => s.CacheWriteTokens).Should().Be(19_807);
        samples.Sum(s => s.Calls).Should().Be(2, "dos llamadas, ni una más por contarlas mejor");

        outcome.Usage!.CacheWriteTokens.Should().Be(19_807, "y el desenlace dice lo mismo");
    }

    /// <summary>El motivo terminal viaja: es el sello que distingue un corte limpio de otra cosa.</summary>
    [Fact]
    public async Task El_motivo_terminal_del_CLI_llega_al_desenlace()
    {
        var samples = new List<UsageSample>();

        ClaudeRunOutcome outcome = await Read(
            samples, Init(),
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"","terminal_reason":"aborted_tools"}""");

        outcome.TerminalReason.Should().Be("aborted_tools");
    }

    // ================================================================ F30 §2 · narrar no toca las cuentas

    /// <summary>
    /// <b>LA CONDICIÓN INNEGOCIABLE DE F30 §2.</b> Leer los eventos de contenido —el texto según se
    /// escribe y los argumentos de la herramienta— no puede mover la condición del corte.
    /// <para>
    /// <b>Por qué es la que da miedo.</b> Los casos nuevos viven en el MISMO <c>switch</c> que
    /// gobierna <see cref="ClaudeStreamReader.AccountingIsComplete"/>, y de esa condición depende si
    /// se corta la pasada. Un corte disparado con una petición en vuelo <b>se factura y no aparece
    /// en ningún sitio</b>: medido en F21, al interrumpir a mitad de respuesta el modelo principal
    /// desaparece entero del <c>modelUsage</c> del evento final. O sea que un fallo aquí no se ve —
    /// se paga.
    /// </para>
    /// <para>
    /// Este test es <see cref="Con_una_peticion_en_vuelo_las_cuentas_NO_estan"/> otra vez, con el
    /// flujo que ahora se lee de verdad: bloques de contenido abriéndose, texto llegando a trozos y
    /// los argumentos de una herramienta escribiéndose. Con todo eso por medio, las cuentas tienen
    /// que decir exactamente lo mismo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Con_los_eventos_de_contenido_por_medio_las_cuentas_dicen_lo_mismo()
    {
        var reader = new ClaudeStreamReader();
        var pipe = new BlockingReader(Init(), Start("msg_1", 2, 11_322, 13_681));

        Task<ClaudeRunOutcome> reading = reader.ReadAsync(pipe, CancellationToken.None);
        await pipe.Drained;

        reader.AccountingIsComplete.Should().BeFalse("hay un mensaje abierto y sin cerrar");

        // Y ahora todo lo que F30 §2 añade a la lectura, con la petición TODAVÍA en vuelo.
        pipe.Push(TextBlockStart(0));
        pipe.Push(TextDelta(0, "Reviso la unidad"));
        pipe.Push(BlockStop(0));
        pipe.Push(ToolBlockStart(1, "mcp__atalaya__submit_findings"));
        pipe.Push(InputDelta(1, """{"findings":[{"title":"Credenciales embebidas"},"""));
        pipe.Push(InputDelta(1, """{"title":"Fuga de stream"}]}"""));
        await pipe.Drained;

        reader.AccountingIsComplete.Should().BeFalse(
            "narrar no cierra ninguna llamada: la petición sigue en vuelo y cortar aquí se pagaría");
        reader.SettledCalls.Should().Be(0);

        pipe.Push(Delta(input: 2, output: 40, cacheRead: 11_322, cacheWrite: 13_681));
        await pipe.Drained;

        reader.AccountingIsComplete.Should().BeTrue("el mensaje cerró y trajo su consumo final");
        reader.SettledCalls.Should().Be(1);

        pipe.Close();
        await reading;
    }

    /// <summary>
    /// <b>Y el coste sale idéntico</b> (F30, «ni un token más»). El mismo flujo, con y sin los
    /// eventos de contenido por medio, tiene que dar las mismas llamadas y los mismos tokens en los
    /// cuatro conceptos. Es la otra mitad de la verificación: la de arriba mira el corte, ésta mira
    /// la factura.
    /// </summary>
    [Fact]
    public async Task Narrar_no_cambia_ni_una_llamada_ni_un_token()
    {
        var sin = new List<UsageSample>();
        ClaudeRunOutcome a = await Read(
            sin,
            Init(),
            Start("msg_1", input: 2, cacheRead: 11_322, cacheWrite: 12_753),
            Assistant("msg_1", input: 2, output: 7),
            Delta(input: 2, output: 6_835, cacheRead: 11_322, cacheWrite: 12_753),
            Result(modelInput: 2, modelOutput: 6_835, cacheRead: 11_322, cacheWrite: 12_753));

        var con = new List<UsageSample>();
        ClaudeRunOutcome b = await Read(
            con,
            Init(),
            Start("msg_1", input: 2, cacheRead: 11_322, cacheWrite: 12_753),
            TextBlockStart(0),
            TextDelta(0, "Reviso "),
            TextDelta(0, "la unidad."),
            BlockStop(0),
            ToolBlockStart(1, "mcp__atalaya__submit_findings"),
            InputDelta(1, """{"findings":[{"title":"Credenciales embebidas"},"""),
            InputDelta(1, """{"title":"Fuga de stream"}]}"""),
            BlockStop(1),
            Assistant("msg_1", input: 2, output: 7),
            Delta(input: 2, output: 6_835, cacheRead: 11_322, cacheWrite: 12_753),
            Result(modelInput: 2, modelOutput: 6_835, cacheRead: 11_322, cacheWrite: 12_753));

        con.Sum(s => s.Calls).Should().Be(sin.Sum(s => s.Calls), "ni una llamada más");
        con.Sum(s => s.InputTokens).Should().Be(sin.Sum(s => s.InputTokens), "ni un token de entrada");
        con.Sum(s => s.OutputTokens).Should().Be(sin.Sum(s => s.OutputTokens), "ni de salida");
        con.Sum(s => s.CacheReadTokens).Should().Be(sin.Sum(s => s.CacheReadTokens));
        con.Sum(s => s.CacheWriteTokens).Should().Be(sin.Sum(s => s.CacheWriteTokens));
        b.ToolCalls.Should().Be(a.ToolCalls);
        b.Usage!.OutputTokens.Should().Be(a.Usage!.OutputTokens);
    }

    /// <summary>
    /// <b>Y lo que se narra es lo que hace falta para que el minuto se entienda</b> (F30 §2): la
    /// herramienta se anuncia al EMPEZAR, y los elementos van apareciendo según se completan.
    /// <para>
    /// <b>El total no viaja, y no es un olvido.</b> Lo que llega es un array que se está
    /// escribiendo: cuántos va a tener no se sabe hasta que cierra. Se cuenta lo que hay; inventar
    /// el denominador sería inventar progreso, que esta fase tiene prohibido.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_herramienta_se_anuncia_al_empezar_y_sus_elementos_segun_se_escriben()
    {
        var narrado = new List<ToolStream>();

        await new ClaudeStreamReader(onTool: narrado.Add).ReadAsync(
            new StringReader(string.Join('\n',
                Init(),
                Start("msg_1", 2, 0, 0),
                ToolBlockStart(0, "mcp__atalaya__submit_findings"),
                InputDelta(0, """{"findings":[{"title":"Credenciales emb"""),
                InputDelta(0, """ebidas","severity":"critica"},"""),
                InputDelta(0, """{"title":"Fuga de stream"}]}"""),
                BlockStop(0))),
            CancellationToken.None);

        narrado[0].Phase.Should().Be(ToolStreamPhase.Started);
        narrado[0].Tool.Should().Be("submit_findings", "sin el prefijo del servidor MCP");
        narrado[0].Items.Should().Be(0, "al empezar no hay nada escrito todavía");

        // Un trozo a mitad de título NO cuenta: se cuenta cuando la comilla cierra.
        List<ToolStream> input = narrado.Where(t => t.Phase == ToolStreamPhase.Input).ToList();
        input.Select(t => t.Items).Should().Equal(1, 2);
        input[0].Last.Should().Be("Credenciales embebidas");
        input[^1].Last.Should().Be("Fuga de stream");
    }

    /// <summary>
    /// <b>El texto se pinta delta a delta y NO se repite</b> (F30 §2). Claude Code lo mandaba
    /// entero al cerrar el mensaje —así que la pantalla se quedaba quieta y luego escupía el
    /// párrafo—, y los trozos sí llegaban: se descartaban. Ahora salen según llegan, y el evento
    /// <c>assistant</c> que los repite al final ya no vuelve a emitirlos.
    /// </summary>
    [Fact]
    public async Task El_texto_llega_delta_a_delta_y_el_mensaje_completo_no_lo_repite()
    {
        var texto = new List<string>();

        await new ClaudeStreamReader(onText: texto.Add).ReadAsync(
            new StringReader(string.Join('\n',
                Init(),
                Start("msg_1", 2, 0, 0),
                TextBlockStart(0),
                TextDelta(0, "Reviso "),
                TextDelta(0, "la unidad."),
                BlockStop(0),
                Assistant("msg_1", input: 2, output: 7),
                Delta(input: 2, output: 7, cacheRead: 0, cacheWrite: 0))),
            CancellationToken.None);

        string.Concat(texto).Should().Be("Reviso la unidad.");
        texto.Should().HaveCount(2, "dos trozos, y el mensaje completo no cuenta como un tercero");
    }

    // ---------------------------------------------------------------- ayudas

    /// <summary>Los eventos CRUDOS de contenido, que son los que F30 §2 empieza a leer.</summary>
    private static string TextBlockStart(int index)
        => "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":"
         + index + ",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}}";

    private static string ToolBlockStart(int index, string name)
        => "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":"
         + index + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\""
         + name + "\",\"input\":{}}}}";

    private static string TextDelta(int index, string text)
        => "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":"
         + index + ",\"delta\":{\"type\":\"text_delta\",\"text\":"
         + System.Text.Json.JsonSerializer.Serialize(text) + "}}}";

    private static string InputDelta(int index, string partial)
        => "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":"
         + index + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":"
         + System.Text.Json.JsonSerializer.Serialize(partial) + "}}}";

    private static string BlockStop(int index)
        => "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":"
         + index + "}}";

    private static Task<ClaudeRunOutcome> Read(List<UsageSample> samples, params string[] lines)
        => new ClaudeStreamReader(onUsage: samples.Add)
            .ReadAsync(new StringReader(string.Join('\n', lines)), CancellationToken.None);

    private static string Init()
        => "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-sonnet-5\","
         + "\"tools\":[\"mcp__atalaya__unit_done\"],"
         + "\"mcp_servers\":[{\"name\":\"atalaya\",\"status\":\"connected\"}]}";

    /// <summary>El evento crudo que ABRE una llamada. Su usage todavía no es el bueno.</summary>
    private static string Start(string id, long input, long cacheRead, long cacheWrite)
        => "{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\""
         + id + "\",\"usage\":{\"input_tokens\":" + input
         + ",\"cache_read_input_tokens\":" + cacheRead
         + ",\"cache_creation_input_tokens\":" + cacheWrite + ",\"output_tokens\":1}}}}";

    /// <summary>El evento crudo que la CIERRA, con su consumo definitivo.</summary>
    private static string Delta(long input, long output, long cacheRead, long cacheWrite)
        => "{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\","
         + "\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"input_tokens\":" + input
         + ",\"output_tokens\":" + output
         + ",\"cache_read_input_tokens\":" + cacheRead
         + ",\"cache_creation_input_tokens\":" + cacheWrite + "}}}";

    private static string Assistant(string id, long input, long output)
        => "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\","
         + "\"content\":[{\"type\":\"text\",\"text\":\".\"}],"
         + "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}}}";

    private static string Result(long modelInput, long modelOutput, long cacheRead, long cacheWrite)
        => "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\","
         + "\"modelUsage\":{\"claude-sonnet-5\":{\"inputTokens\":" + modelInput
         + ",\"outputTokens\":" + modelOutput
         + ",\"cacheReadInputTokens\":" + cacheRead
         + ",\"cacheCreationInputTokens\":" + cacheWrite + "}}}";

    /// <summary>
    /// Un flujo que se puede alimentar a trozos, para poder preguntar por el estado del lector
    /// <b>mientras la sesión corre</b> — que es cuando el corte lo pregunta.
    /// </summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly Queue<string> _lines = new();
        private readonly SemaphoreSlim _available = new(0);
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _closed;

        public BlockingReader(params string[] lines)
        {
            foreach (string line in lines)
            {
                Push(line);
            }
        }

        /// <summary>Se cumple cuando el lector ha consumido todo lo que hay puesto.</summary>
        public Task Drained => _drained.Task;

        public void Push(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                if (_drained.Task.IsCompleted)
                {
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            _available.Release();
        }

        public override void Close()
        {
            _closed = true;
            _available.Release();
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            lock (_lines)
            {
                if (_lines.Count == 0)
                {
                    _drained.TrySetResult();
                }
            }

            await _available.WaitAsync(ct);
            lock (_lines)
            {
                if (_lines.Count > 0)
                {
                    // Ojo: aquí NO se señala «consumido». La línea todavía no la ha PROCESADO el
                    // lector — solo se la estamos entregando. Señalarlo aquí dejaba al test
                    // afirmando sobre un evento que aún no había ocurrido, y fallaba una de cada
                    // cinco veces. Se señala arriba, cuando el lector vuelve a pedir la siguiente.
                    return _lines.Dequeue();
                }
            }

            return _closed ? null : string.Empty;
        }
    }
}
