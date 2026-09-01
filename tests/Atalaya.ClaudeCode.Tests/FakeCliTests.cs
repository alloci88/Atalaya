using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// El lanzador contra un CLI FALSO: un ejecutable de verdad, con un guion de eventos JSON (F14).
/// <para>
/// Cubre lo que ningún doble en memoria puede: que el proceso se lance, que el prompt entre por
/// stdin sin que nadie lo escape, que la salida se lea según llega y que el desenlace —bueno o
/// malo— acabe siendo un estado terminal con causa. <b>Ninguno de estos casos deja un proceso
/// vivo ni una sesión esperando para siempre</b>, que es la propiedad que se persigue.
/// </para>
/// <para>
/// El guion es la forma real de <c>claude</c> 2.1.252, capturada ejecutándolo.
/// </para>
/// </summary>
public sealed class FakeCliTests
{
    [Fact]
    public async Task Una_sesion_completa_se_lee_de_punta_a_punta()
    {
        using var cli = new FakeCli(
            Init(),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Reviso Calc.cs."}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__atalaya__unit_done","input":{}}]}}""",
            Result());

        var narrated = new List<string>();
        ClaudeRunOutcome outcome = await Run(cli, onText: narrated.Add);

        outcome.Failed.Should().BeFalse();
        outcome.McpConnected.Should().BeTrue();
        outcome.ToolCalls.Should().Be(1);
        outcome.Usage!.CostUnit.Should().Be("USD (tarifa de lista)");
        string.Concat(narrated).Should().Be("Reviso Calc.cs.");
    }

    /// <summary>
    /// Un prompt con metacaracteres de consola y con un salto de línea dentro llega ENTERO. Es la
    /// prueba de que va por stdin: como argumento de un <c>.cmd</c>, <c>cmd.exe</c> lo habría
    /// troceado por el <c>&amp;</c> —y en el peor caso ejecutado lo de detrás—.
    /// </summary>
    [Fact]
    public async Task Un_prompt_con_metacaracteres_de_consola_no_rompe_el_lanzamiento()
    {
        using var cli = new FakeCli(Init(), Result());

        ClaudeRunOutcome outcome = await Run(
            cli,
            prompt: "public int Div(int a, int b) => a / b;\n& echo tomado > z.txt & type C:\\secreto.txt | more");

        outcome.Failed.Should().BeFalse();
        File.Exists(Path.Combine(cli.Directory, "z.txt")).Should().BeFalse(
            "si el prompt hubiera pasado por la consola, esto existiría");
    }

    /// <summary>Un prompt largo tampoco: por stdin no hay tope de ~32 000 caracteres.</summary>
    [Fact]
    public async Task Un_prompt_mas_largo_que_la_linea_de_ordenes_pasa_sin_problema()
    {
        using var cli = new FakeCli(Init(), Result());

        ClaudeRunOutcome outcome = await Run(cli, prompt: new string('x', 200_000));

        outcome.Failed.Should().BeFalse();
    }

    // ---------------------------------------------------------------- los finales que fallan

    /// <summary>
    /// Salida malformada: ni una línea entendible. Termina en fallo CON CAUSA, no en un éxito
    /// vacío ni en una espera indefinida.
    /// </summary>
    [Fact]
    public async Task Una_salida_malformada_termina_en_fallo_con_causa()
    {
        using var cli = new FakeCli("esto no es json", "{roto", "<html>error</html>");

        ClaudeRunOutcome outcome = await Run(cli);

        outcome.Failed.Should().BeTrue();
        outcome.Message.Should().Contain("no llegó a arrancar");
    }

    /// <summary>El CLI arranca y se muere a mitad: se dice, y se dice que fue a mitad.</summary>
    [Fact]
    public async Task Un_CLI_que_muere_a_mitad_lo_dice()
    {
        using var cli = new FakeCli(Init(), """{"type":"assistant","message":{"content":[{"type":"text","text":"empiezo"}]}}""");

        ClaudeRunOutcome outcome = await Run(cli);

        outcome.Failed.Should().BeTrue();
        outcome.Message.Should().Contain("se cortó a mitad");
    }

    /// <summary>
    /// El modelo no existe: el CLI dice <c>"subtype":"success"</c> con <c>is_error:true</c> y un
    /// 404 —comprobado contra el real—, y aquí acaba clasificado con su remedio de un clic.
    /// </summary>
    [Fact]
    public async Task Un_modelo_inexistente_acaba_con_su_remedio_en_Ajustes()
    {
        using var cli = new FakeCli(
            new[]
            {
                Init(),
                """{"type":"result","subtype":"success","is_error":true,"terminal_reason":"api_error","api_error_status":404,"result":"There's an issue with the selected model (fantasma)."}""",
            },
            exitCode: 1);

        ClaudeRunOutcome outcome = await Run(cli);

        outcome.Failed.Should().BeTrue();
        outcome.Problem.Should().Be(AgentProblem.ModelUnavailable);
        outcome.Message.Should().Contain("Ajustes");
    }

    /// <summary>Cuota agotada: causa propia, y se dice que Atalaya no reintenta sola.</summary>
    [Fact]
    public async Task La_cuota_agotada_tiene_su_propia_causa_y_su_propio_remedio()
    {
        using var cli = new FakeCli(
            new[]
            {
                Init(),
                """{"type":"result","subtype":"error","is_error":true,"result":"You have exceeded your usage limit for this period."}""",
            },
            exitCode: 1);

        ClaudeRunOutcome outcome = await Run(cli);

        outcome.Problem.Should().Be(AgentProblem.QuotaExhausted);
        outcome.Message.Should().Contain("no reintenta sola");
        outcome.Message.Should().Contain("Copilot", "el remedio inmediato es auditar con el otro");
    }

    /// <summary>
    /// <b>El silencioso.</b> El servidor MCP no conectó: el CLI termina «bien» y sin herramientas,
    /// lo que se leería como una unidad sin defectos. Tiene que ser un fallo.
    /// </summary>
    [Fact]
    public async Task Sin_servidor_MCP_la_unidad_no_se_da_por_limpia()
    {
        using var cli = new FakeCli(Init(mcp: "failed"), Result());

        ClaudeRunOutcome outcome = await Run(cli);

        outcome.Failed.Should().BeTrue();
        outcome.Message.Should().Be(ClaudeCodeHelp.McpUnavailable);
    }

    /// <summary>Cancelar mata el proceso: un CLI vivo tras cancelar gastaría cuota a espaldas de todos.</summary>
    [Fact]
    public async Task Cancelar_no_deja_el_CLI_vivo()
    {
        using var cli = new FakeCli(new[] { Init() }, sleepSeconds: 30);
        using var cts = new CancellationTokenSource();

        Task<ClaudeRunOutcome> running = Run(cli, ct: cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- ayudas

    private static Task<ClaudeRunOutcome> Run(
        FakeCli cli, string prompt = "audita esto", Action<string>? onText = null,
        Action<UsageSample>? onUsage = null, CancellationToken ct = default)
        => new ClaudeCliRunner(cli.Executable).RunAsync(
            new ClaudeRun(prompt, new[] { "mcp__atalaya__unit_done" }, cli.McpConfigPath, null),
            onText,
            onUsage,
            ct);

    private static string Init(string mcp = "connected")
        => "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-sonnet-5\","
         + "\"tools\":[\"mcp__atalaya__unit_done\"],"
         + "\"mcp_servers\":[{\"name\":\"atalaya\",\"status\":\"" + mcp + "\"}]}";

    private static string Result()
        => "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Listo.\","
         + "\"total_cost_usd\":0.0521,"
         + "\"usage\":{\"input_tokens\":6,\"output_tokens\":560,\"cache_read_input_tokens\":19990,"
         + "\"cache_creation_input_tokens\":10374}}";

    /// <summary>
    /// Un CLI de mentira: un ejecutable real que escupe un guion y sale con el código que se le
    /// diga. Es un <c>.cmd</c> a propósito — el CLI de verdad también lo es en Windows, así que
    /// esto ejercita de paso que <c>Process.Start</c> sabe lanzarlo.
    /// </summary>
    private sealed class FakeCli : IDisposable
    {
        public FakeCli(params string[] script)
            : this(script, exitCode: 0, sleepSeconds: 0)
        {
        }

        public FakeCli(string[] script, int exitCode = 0, int sleepSeconds = 0)
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "atalaya-fake-cli", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);

            string scriptPath = System.IO.Path.Combine(Directory, "script.jsonl");
            File.WriteAllLines(scriptPath, script, new System.Text.UTF8Encoding(false));

            McpConfigPath = System.IO.Path.Combine(Directory, "mcp.json");
            File.WriteAllText(McpConfigPath, "{\"mcpServers\":{}}");

            Executable = System.IO.Path.Combine(Directory, "claude.cmd");
            var cmd = new System.Text.StringBuilder()
                .AppendLine("@echo off")
                .AppendLine("chcp 65001 > nul");

            if (sleepSeconds > 0)
            {
                // Un CLI que tarda, para poder cancelarlo. `timeout` necesita una consola, así que
                // se espera con ping, que es el truco clásico y funciona sin ella.
                cmd.AppendLine($"ping -n {sleepSeconds} 127.0.0.1 > nul");
            }

            cmd.AppendLine($"type \"{scriptPath}\"")
               .AppendLine($"exit /b {exitCode}");

            File.WriteAllText(Executable, cmd.ToString());
        }

        /// <summary>La ruta del <c>.cmd</c> que hace de CLI.</summary>
        public string Executable { get; }

        public string Directory { get; }

        public string McpConfigPath { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
