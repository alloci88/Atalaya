using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// El transporte entero, con procesos y tuberías DE VERDAD (F14).
/// <para>
/// Lo que se ejercita aquí es la cadena que en producción va
/// <c>claude → Atalaya.Mcp (stdio) → tubería con nombre → AtalayaMcpServer → toolbox</c>, con el
/// puente lanzado como proceso real. Es la mitad del diseño que no se puede probar con dobles: que
/// el relé conecte, que no se coma ni un byte, que descargue cada mensaje en vez de esperar a
/// llenar un buffer —con buffer, el CLI se quedaría esperando una respuesta ya escrita— y que
/// termine solo cuando su entrada se cierra.
/// </para>
/// <para>
/// Aquí el papel del CLI lo hace el test: escribe las tramas MCP en el stdin del puente y lee sus
/// respuestas por stdout, que es exactamente lo que hace <c>claude</c>.
/// </para>
/// </summary>
public sealed class McpBridgeTests
{
    /// <summary>El puente viaja en la carpeta de salida; el <c>.csproj</c> lo copia.</summary>
    private static string BridgePath => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "Atalaya.Mcp.exe" : "Atalaya.Mcp");

    [Fact]
    public void El_puente_se_publica_junto_a_la_aplicacion()
        => File.Exists(BridgePath).Should().BeTrue(
            "sin el ejecutable al lado, `claude` no tiene servidor MCP que lanzar");

    /// <summary>
    /// El circuito completo: el puente conecta, el «CLI» pide el catálogo, llama a dos tools y las
    /// respuestas vuelven. Si algo del transporte estuviera mal —el orden, el vaciado del buffer,
    /// el fin de línea— este test se cuelga o falla, que es justo lo que se quiere de él.
    /// </summary>
    [Fact]
    public async Task Una_conversacion_entera_viaja_del_puente_al_toolbox_y_vuelve()
    {
        var toolbox = new RecordingToolbox();
        await using var host = new McpPipeHost(AuditorTools.ForAudit(toolbox));
        host.Start();

        using Process bridge = StartBridge(host.PipeName);
        try
        {
            await Send(bridge, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
            JsonNode init = await Receive(bridge);
            init["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("atalaya");

            await Send(bridge, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            await Send(bridge, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            JsonNode list = await Receive(bridge);
            list["result"]!["tools"]!.AsArray().Should().HaveCount(6);

            await Send(bridge, """
                {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"submit_finding",
                 "arguments":{"ruleId":"nulos","pillar":"Robustez","severity":"alta","title":"Divide sin comprobar cero",
                 "description":"d","impact":"i","recommendation":"r",
                 "locations":[{"path":"Calc.cs","line":12,"snippet":"a / b"}]}}}
                """.ReplaceLineEndings(string.Empty));
            JsonNode submitted = await Receive(bridge);
            submitted["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();

            await Send(bridge, """
                {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"unit_done",
                 "arguments":{"unitPath":"Calc.cs","summary":"1 hallazgo"}}}
                """.ReplaceLineEndings(string.Empty));
            JsonNode done = await Receive(bridge);
            done["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();

            // Y el hallazgo llegó al toolbox de la aplicación, cruzando dos procesos.
            toolbox.Findings.Should().ContainSingle()
                .Which.Title.Should().Be("Divide sin comprobar cero");
            toolbox.UnitPath.Should().Be("Calc.cs");
            host.ToolCalls.Should().Be(2);
        }
        finally
        {
            Stop(bridge);
        }
    }

    /// <summary>
    /// El puente termina solo cuando el «CLI» cierra su entrada. Un relé que se quedara vivo sería
    /// un proceso colgado por sesión, y con una auditoría de cincuenta unidades eso son cincuenta.
    /// </summary>
    [Fact]
    public async Task El_puente_termina_cuando_el_CLI_cierra_su_entrada()
    {
        await using var host = new McpPipeHost(AuditorTools.ForAudit(new RecordingToolbox()));
        host.Start();

        using Process bridge = StartBridge(host.PipeName);
        await Send(bridge, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{}}""");
        await Receive(bridge);

        bridge.StandardInput.Close();

        bool exited = bridge.WaitForExit(10_000);
        exited.Should().BeTrue("un relé que no se muere deja un proceso por sesión");
        bridge.ExitCode.Should().Be(0);
    }

    /// <summary>
    /// Sin nadie escuchando, el puente NO se queda esperando para siempre: se muere con un código
    /// distinto de 0, el CLI marca su servidor MCP como «failed», y el driver para la sesión antes
    /// de gastar. Colgarse aquí sería la peor de las opciones — una auditoría que nunca vuelve.
    /// </summary>
    [Fact]
    public void Sin_nadie_escuchando_el_puente_se_rinde_en_vez_de_colgarse()
    {
        using Process bridge = StartBridge("atalaya-mcp-no-existe-nadie-aqui");

        bridge.WaitForExit(30_000).Should().BeTrue("el puente tiene un plazo de conexión, no espera eternamente");
        bridge.ExitCode.Should().NotBe(0);
        bridge.StandardError.ReadToEnd().Should().Contain("no está escuchando");
    }

    /// <summary>Ejecutado a mano no hace nada raro: lo dice y se va.</summary>
    [Fact]
    public void Sin_argumentos_el_puente_explica_que_no_se_ejecuta_a_mano()
    {
        using Process bridge = StartBridge();

        bridge.WaitForExit(10_000).Should().BeTrue();
        bridge.ExitCode.Should().Be(2);
        bridge.StandardError.ReadToEnd().Should().Contain("no se ejecuta a mano");
    }

    // ---------------------------------------------------------------- ayudas

    private static Process StartBridge(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BridgePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return Process.Start(psi)!;
    }

    private static async Task Send(Process bridge, string frame)
    {
        await bridge.StandardInput.WriteAsync(frame + "\n");
        await bridge.StandardInput.FlushAsync();
    }

    private static async Task<JsonNode> Receive(Process bridge)
    {
        Task<string?> line = bridge.StandardOutput.ReadLineAsync();
        Task done = await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(15)));

        done.Should().Be(line, "el puente tiene que contestar; si no, el transporte está roto");
        return JsonNode.Parse((await line)!)!;
    }

    private static void Stop(Process bridge)
    {
        try
        {
            if (!bridge.HasExited)
            {
                bridge.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class RecordingToolbox : IAuditToolbox
    {
        public List<SubmitFindingArgs> Findings { get; } = new();

        public string? UnitPath { get; private set; }

        public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
        {
            Findings.Add(args);
            return new SubmitFindingResult(true);
        }

        public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
        {
            Findings.AddRange(findings);
            return new SubmitFindingsResult(findings.Select(_ => new SubmitFindingResult(true)).ToList());
        }

        public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
            => new(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations) => new(true);

        public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
            => UnitPath = unitPath;

        public string ReadSignatures(string path) => "// firmas";
    }
}
