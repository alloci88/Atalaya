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

    // ---------------------------------------------------------------- ayudas

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
