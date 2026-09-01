using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// F16-RETOQUE §1 — EL CONSUMO, SEGÚN OCURRE Y CUADRADO AL FINAL.
/// <para>
/// El pie de una sesión de arreglo con Claude se quedaba en «0 llamadas · coste no calculable (sin
/// tokens registrados)» con el agente ya trabajando. La causa no era que el arreglo no reenviara
/// los eventos: era que el consumo <b>solo se leía del evento que cierra el turno</b>, y con esta
/// casa un arreglo entero —leer, preguntar, editar, compilar— cabe en UN turno. Hasta el final no
/// había nada que enseñar.
/// </para>
/// <para>
/// <b>Y las tres cifras del evento final no dicen lo mismo</b>, cosa que hubo que medir contra el
/// CLI real (2.1.252), porque en una sesión de un solo turno y sin herramientas no se distingue.
/// Cada test de abajo fija una de las tres.
/// </para>
/// </summary>
public sealed class ClaudeUsageStreamTests
{
    /// <summary>
    /// Una muestra por llamada, en cuanto llega. Es lo único que hace que el pie se mueva mientras
    /// el agente trabaja.
    /// </summary>
    [Fact]
    public async Task Cada_llamada_al_modelo_informa_su_consumo_sin_esperar_al_final()
    {
        var samples = new List<UsageSample>();

        await Read(samples, Init(), Assistant("a", input: 10, output: 4), Assistant("b", input: 10, output: 6));

        samples.Should().HaveCount(2, "una por llamada, y el turno ni siquiera se ha cerrado");
        samples.Should().OnlyContain(s => s.Calls == 1);
        samples[0].InputTokens.Should().Be(10);
        samples[1].OutputTokens.Should().Be(6);
    }

    /// <summary>
    /// <b>El mismo mensaje repite su <c>usage</c> en cada bloque de contenido.</b> Medido: una
    /// respuesta con pensamiento, texto y llamada a herramienta llega como tres eventos con el
    /// mismo <c>id</c> y el mismo consumo. Contarlos por evento multiplicaba el gasto por tres.
    /// </summary>
    [Fact]
    public async Task Un_mensaje_partido_en_bloques_se_cuenta_UNA_vez()
    {
        var samples = new List<UsageSample>();

        await Read(
            samples,
            Init(),
            Assistant("msg_1", input: 10, output: 4),
            Assistant("msg_1", input: 10, output: 4),
            Assistant("msg_1", input: 10, output: 4));

        samples.Should().ContainSingle("es una llamada, aunque hayan hecho falta tres eventos para contarla");
        samples[0].InputTokens.Should().Be(10);
    }

    /// <summary>
    /// <b>Manda <c>modelUsage</c>, que es el agregado ACUMULADO de la invocación.</b> Es el único
    /// que reproduce el coste que el propio CLI calcula: en una sesión real de cuatro llamadas
    /// declaraba <c>in 986</c> donde los eventos sumaban 40, y solo con el 986 salía su
    /// <c>total_cost_usd</c> exacto. Lo que va llegando llamada a llamada es un anticipo, y al
    /// cerrar el turno se cuadra la diferencia.
    /// </summary>
    [Fact]
    public async Task El_agregado_del_CLI_manda_y_la_diferencia_se_cuadra_al_cerrar_el_turno()
    {
        var samples = new List<UsageSample>();

        ClaudeRunOutcome outcome = await Read(
            samples,
            Init(),
            Assistant("a", input: 10, output: 4),
            Result(turnInput: 10, turnOutput: 4, modelInput: 910, modelOutput: 20, cost: 0.5m));

        samples.Should().HaveCount(2, "la llamada y el ajuste");
        samples[1].InputTokens.Should().Be(900, "910 declarados menos los 10 ya reportados");
        samples[1].OutputTokens.Should().Be(16);
        samples[1].Calls.Should().Be(0, "el ajuste no es una llamada nueva: es la misma contada mejor");

        samples.Sum(s => s.InputTokens).Should().Be(910, "quien suma lo que le llega tiene el agregado bueno");
        samples.Sum(s => s.Calls).Should().Be(1);
        outcome.Usage!.InputTokens.Should().Be(910, "y el desenlace dice lo mismo que las muestras");
    }

    /// <summary>
    /// Sin <c>modelUsage</c> —un CLI más viejo, otra forma de salida— se cae al <c>usage</c> del
    /// evento final, que es del turno y se va acumulando a mano. Es lo que había antes de esto.
    /// </summary>
    [Fact]
    public async Task Sin_agregado_declarado_se_acumula_el_usage_de_cada_turno()
    {
        var samples = new List<UsageSample>();

        ClaudeRunOutcome outcome = await Read(
            samples,
            Init(),
            """{"type":"result","subtype":"success","is_error":false,"result":"ok","usage":{"input_tokens":7,"output_tokens":3}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"ok","usage":{"input_tokens":5,"output_tokens":2}}""");

        outcome.Usage!.InputTokens.Should().Be(12);
        outcome.Usage.OutputTokens.Should().Be(5);
        samples.Sum(s => s.Calls).Should().Be(2,
            "un turno sin muestras propias cuenta como una llamada: haberlas, las hubo");
    }

    /// <summary>
    /// El coste viene ACUMULADO y el de un turno es la diferencia. Sumar la cifra de cada turno
    /// contaría el primero tantas veces como turnos hubiera.
    /// </summary>
    [Fact]
    public async Task El_coste_de_un_turno_es_la_diferencia_contra_el_acumulado()
    {
        var samples = new List<UsageSample>();

        ClaudeRunOutcome outcome = await Read(
            samples,
            Init(),
            Result(turnInput: 10, turnOutput: 4, modelInput: 10, modelOutput: 4, cost: 0.30m),
            Result(turnInput: 10, turnOutput: 4, modelInput: 20, modelOutput: 8, cost: 0.50m));

        samples.Where(s => s.Cost is not null).Select(s => s.Cost).Should().Equal(0.30m, 0.20m);
        outcome.Usage!.Cost.Should().Be(0.50m, "y el total vuelve a ser el acumulado que declaró el CLI");
        outcome.Usage.CostUnit.Should().Be(ClaudeUsage.ListPriceUnit);
    }

    /// <summary>
    /// Y si el acumulado se quedara por debajo de lo ya reportado, la diferencia se queda en cero.
    /// Un consumo negativo se propagaría a los agregados sin que nadie lo notara — el mismo
    /// blindaje que la fórmula de credits (D-785).
    /// </summary>
    [Fact]
    public async Task Nunca_se_informa_un_consumo_negativo()
    {
        var samples = new List<UsageSample>();

        await Read(
            samples,
            Init(),
            Assistant("a", input: 100, output: 50),
            Result(turnInput: 1, turnOutput: 1, modelInput: 1, modelOutput: 1, cost: 0m));

        samples.Should().OnlyContain(s => s.InputTokens >= 0 && s.OutputTokens >= 0);
        samples[1].InputTokens.Should().Be(0);
    }

    /// <summary>
    /// Un proveedor que no informa consumo no informa consumo: no se le inventa ninguno. Es el
    /// único caso en que «sin tokens registrados» es verdad.
    /// </summary>
    [Fact]
    public async Task Un_CLI_que_no_informa_consumo_no_inventa_ninguno()
    {
        var samples = new List<UsageSample>();

        ClaudeRunOutcome outcome = await Read(
            samples,
            Init(),
            """{"type":"assistant","message":{"id":"a","content":[{"type":"text","text":"hola"}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"ok"}""");

        outcome.Usage!.InputTokens.Should().Be(0);
        outcome.Usage.Cost.Should().BeNull();
        outcome.Usage.CostUnit.Should().BeNull();
    }

    // ---------------------------------------------------------------- ayudas

    private static Task<ClaudeRunOutcome> Read(List<UsageSample> samples, params string[] lines)
        => new ClaudeStreamReader(onUsage: samples.Add)
            .ReadAsync(new StringReader(string.Join('\n', lines)), CancellationToken.None);

    private static string Init()
        => "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\","
         + "\"tools\":[\"mcp__atalaya__fix_done\"],"
         + "\"mcp_servers\":[{\"name\":\"atalaya\",\"status\":\"connected\"}]}";

    /// <summary>Una respuesta del modelo con su consumo. El <c>id</c> es lo que la identifica.</summary>
    private static string Assistant(string id, int input, int output)
        => "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\","
         + "\"content\":[{\"type\":\"text\",\"text\":\".\"}],"
         + "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}}}";

    /// <summary>
    /// El cierre de un turno con las tres cifras que informa el CLI real: el <c>usage</c> del
    /// turno, el <c>modelUsage</c> acumulado de la invocación y el coste, también acumulado.
    /// </summary>
    private static string Result(
        int turnInput, int turnOutput, int modelInput, int modelOutput, decimal cost)
        => "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\","
         + "\"total_cost_usd\":" + cost.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
         + "\"usage\":{\"input_tokens\":" + turnInput + ",\"output_tokens\":" + turnOutput + "},"
         + "\"modelUsage\":{\"claude-opus-5\":{\"inputTokens\":" + modelInput
         + ",\"outputTokens\":" + modelOutput + "}}}";
}
