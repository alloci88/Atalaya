using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// La lectura del flujo <c>--output-format stream-json</c> del CLI (F14).
/// <para>
/// <b>Los guiones de estos tests no están inventados: son la forma real que emite
/// <c>claude</c> 2.1.252</b>, recortada a los campos que se leen. Se ejecutó el CLI de verdad —con
/// un servidor MCP de mentira— para capturar cada uno de los desenlaces que aparecen aquí, y de esa
/// captura salieron las tres sorpresas que el driver tiene que absorber: que <c>subtype</c> dice
/// «success» cuando la sesión murió, que hay líneas en la salida que no son JSON, y que un servidor
/// MCP caído produce una sesión «con éxito» y vacía.
/// </para>
/// </summary>
public sealed class ClaudeStreamTests
{
    // ---------------------------------------------------------------- el camino bueno

    [Fact]
    public async Task Una_sesion_normal_trae_su_modelo_sus_llamadas_y_su_uso()
    {
        ClaudeRunOutcome outcome = await Read(
            Init(mcp: "connected", tools: new[] { "mcp__atalaya__submit_finding", "mcp__atalaya__unit_done" }),
            Assistant(text: "Miro la division."),
            AssistantToolUse("mcp__atalaya__submit_finding"),
            AssistantToolUse("mcp__atalaya__unit_done"),
            ResultOk());

        outcome.Failed.Should().BeFalse();
        outcome.McpConnected.Should().BeTrue();
        outcome.Model.Should().Be("claude-sonnet-5");
        outcome.ToolCalls.Should().Be(2);
        outcome.Tools.Should().Contain("mcp__atalaya__unit_done");
    }

    [Fact]
    public async Task El_texto_del_auditor_se_emite_para_la_columna_de_actividad()
    {
        var narrated = new List<string>();
        await Read(new ClaudeStreamReader(narrated.Add),
            Init(),
            Assistant(text: "Reviso Calc.cs."),
            Assistant(text: " Nada mas."),
            ResultOk());

        string.Concat(narrated).Should().Be("Reviso Calc.cs. Nada mas.");
    }

    /// <summary>
    /// El pensamiento NO se narra. Copilot tampoco lo manda, y enseñar a uno razonando y al otro no
    /// haría que parecieran distintos por el continente en vez de por el fondo.
    /// </summary>
    [Fact]
    public async Task El_pensamiento_no_se_narra()
    {
        var narrated = new List<string>();
        await Read(new ClaudeStreamReader(narrated.Add),
            Init(),
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"mmm"},{"type":"text","text":"Listo."}]}}""",
            ResultOk());

        string.Concat(narrated).Should().Be("Listo.");
    }

    /// <summary>
    /// El coste llega con SU unidad puesta. El CLI da dólares de tarifa de lista —lo etiqueta él
    /// mismo <c>costBasis: "list"</c>— y una suscripción no cobra por llamada: sin la unidad
    /// pegada, esa cifra acabaría sumada con las peticiones premium de Copilot.
    /// </summary>
    [Fact]
    public async Task El_coste_se_registra_con_su_unidad_para_no_poder_mezclarse()
    {
        ClaudeRunOutcome outcome = await Read(Init(), ResultOk());

        outcome.Usage.Should().NotBeNull();
        outcome.Usage!.Cost.Should().Be(0.052181m);
        outcome.Usage.CostUnit.Should().Be("USD (tarifa de lista)");
        outcome.Usage.InputTokens.Should().Be(6);
        outcome.Usage.OutputTokens.Should().Be(560);
        outcome.Usage.CacheReadTokens.Should().Be(19990);
        outcome.Usage.CacheWriteTokens.Should().Be(10374);
    }

    [Fact]
    public async Task Sin_coste_informado_no_se_inventa_ninguno()
    {
        ClaudeRunOutcome outcome = await Read(
            Init(),
            """{"type":"result","subtype":"success","is_error":false,"result":"ok","usage":{"input_tokens":5,"output_tokens":9}}""");

        outcome.Usage!.Cost.Should().BeNull();
        outcome.Usage.CostUnit.Should().BeNull("«no informado» no es cero");
        outcome.Usage.InputTokens.Should().Be(5);
    }

    // ---------------------------------------------------------------- lo que el CLI real enseñó

    /// <summary>
    /// <b>La sorpresa número uno.</b> Un modelo inexistente devuelve <c>"subtype":"success"</c> con
    /// <c>is_error:true</c> y un 404. Creerle al <c>subtype</c> daría por buena una sesión que
    /// nunca corrió.
    /// </summary>
    [Fact]
    public async Task Manda_is_error_y_no_el_subtype_que_dice_success()
    {
        ClaudeRunOutcome outcome = await Read(
            Init(),
            """{"type":"result","subtype":"success","is_error":true,"terminal_reason":"api_error","api_error_status":404,"result":"There's an issue with the selected model (no-existe). It may not exist or you may not have access to it."}""");

        outcome.Failed.Should().BeTrue();
        outcome.Problem.Should().Be(AgentProblem.ModelUnavailable);
        outcome.Message.Should().Contain("Ajustes", "el remedio es de un clic y hay que decir dónde");
    }

    /// <summary>
    /// <b>La sorpresa número dos.</b> En la salida aparecen líneas que no son JSON —el CLI escribe
    /// <c>[claude-code:unrecognized_model] {...}</c> en stderr—. Un parser que se rompiera con eso
    /// convertiría cada versión nueva del CLI en una avería.
    /// </summary>
    [Fact]
    public async Task Las_lineas_que_no_son_eventos_se_ignoran_sin_ruido()
    {
        ClaudeRunOutcome outcome = await Read(
            "[claude-code:unrecognized_model] {\"model\":\"x\"}",
            Init(),
            """{"type":"rate_limit_event","data":{}}""",
            """{"type":"system","subtype":"thinking_tokens","tokens":120}""",
            "",
            ResultOk());

        outcome.Failed.Should().BeFalse();
        outcome.Model.Should().Be("claude-sonnet-5");
    }

    /// <summary>
    /// Un flujo que se acaba sin <c>result</c> es el CLI muriéndose a media sesión. Termina en
    /// estado terminal con causa — jamás en un éxito silencioso ni en una espera eterna.
    /// </summary>
    [Fact]
    public async Task Un_flujo_que_se_corta_sin_resultado_es_un_fallo_con_causa()
    {
        ClaudeRunOutcome outcome = await Read(Init(), Assistant(text: "empiezo"));

        outcome.Failed.Should().BeTrue();
        outcome.Message.Should().Contain("se cortó a mitad");
    }

    [Fact]
    public async Task Un_flujo_vacio_dice_que_la_sesion_no_llego_a_arrancar()
    {
        ClaudeRunOutcome outcome = await Read();

        outcome.Failed.Should().BeTrue();
        outcome.Message.Should().Contain("no llegó a arrancar");
    }

    // ---------------------------------------------------------------- la trampa de verdad

    /// <summary>
    /// <b>La sorpresa número tres, y la más peligrosa.</b> Con el servidor MCP caído, el CLI sigue
    /// adelante: el modelo se queda sin herramientas, no puede reportar nada, y la sesión termina
    /// con <c>is_error:false</c> y <c>tools:[]</c>. Eso se leería como <b>una unidad sin defectos</b>.
    /// <para>
    /// Se convierte en fallo explícito, porque «no hay defectos» y «no se pudo mirar» no pueden
    /// verse igual: lo primero cierra una unidad y lo segundo tiene que pararla.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sin_servidor_MCP_la_sesion_es_un_fallo_aunque_el_CLI_diga_que_fue_bien()
    {
        ClaudeRunOutcome raw = await Read(
            Init(mcp: "failed", tools: Array.Empty<string>()),
            ResultOk());

        raw.Failed.Should().BeFalse("el CLI, por su cuenta, dice que todo fue bien");
        raw.McpConnected.Should().BeFalse();

        ClaudeRunOutcome explained = ClaudeCliRunner.Explain(raw, exitCode: 0, stderr: string.Empty);

        explained.Failed.Should().BeTrue();
        explained.Message.Should().Be(ClaudeCodeHelp.McpUnavailable);
        explained.Message.Should().Contain("antes de gastar");
    }

    /// <summary>
    /// Y que esté conectado OTRO servidor MCP del usuario no cuenta: el que trae las herramientas
    /// de auditoría es el de Atalaya, y se pregunta por su nombre.
    /// </summary>
    [Fact]
    public async Task Otro_servidor_MCP_conectado_no_vale_por_el_de_Atalaya()
    {
        ClaudeRunOutcome outcome = await Read(
            """{"type":"system","subtype":"init","model":"claude-sonnet-5","tools":[],"mcp_servers":[{"name":"otro","status":"connected"}]}""",
            ResultOk());

        outcome.McpConnected.Should().BeFalse();
    }

    /// <summary>
    /// Un CLI que sale con código distinto de 0 sin haberlo dicho en el flujo se cuenta con lo que
    /// haya —el crudo de stderr— y sin proponer una causa (N-2).
    /// </summary>
    [Fact]
    public async Task Una_salida_con_codigo_de_error_se_cuenta_con_el_crudo_delante()
    {
        ClaudeRunOutcome raw = await Read(Init(), ResultOk());

        ClaudeRunOutcome explained = ClaudeCliRunner.Explain(raw, exitCode: 1, stderr: "algo raro paso");

        explained.Failed.Should().BeTrue();
        explained.Message.Should().Contain("algo raro paso");
        explained.Message.Should().Contain("no se lo inventa");
    }

    // ---------------------------------------------------------------- ayudas

    private static Task<ClaudeRunOutcome> Read(params string[] lines)
        => Read(new ClaudeStreamReader(), lines);

    private static Task<ClaudeRunOutcome> Read(ClaudeStreamReader reader, params string[] lines)
        => reader.ReadAsync(new StringReader(string.Join('\n', lines)), CancellationToken.None);

    private static string Init(string mcp = "connected", string[]? tools = null)
    {
        string toolList = string.Join(",", (tools ?? new[] { "mcp__atalaya__unit_done" }).Select(t => $"\"{t}\""));
        return $$"""
            {"type":"system","subtype":"init","session_id":"abc","model":"claude-sonnet-5",
             "tools":[{{toolList}}],"mcp_servers":[{"name":"atalaya","status":"{{mcp}}"}]}
            """.ReplaceLineEndings(string.Empty);
    }

    private static string Assistant(string text)
        => "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string AssistantToolUse(string name)
        => "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\""
           + name + "\",\"input\":{}}]}}";

    /// <summary>El evento final tal y como lo emite el CLI real, recortado a lo que se lee.</summary>
    private static string ResultOk()
        => """
           {"type":"result","subtype":"success","is_error":false,"stop_reason":"end_turn",
            "total_cost_usd":0.052181,"num_turns":3,"result":"Listo.",
            "usage":{"input_tokens":6,"output_tokens":560,"cache_read_input_tokens":19990,
                     "cache_creation_input_tokens":10374}}
           """.ReplaceLineEndings(string.Empty);
}
