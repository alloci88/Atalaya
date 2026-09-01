using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// El servidor MCP de Atalaya, sobre streams y sin lanzar un proceso (F14).
/// <para>
/// La conversación que se ejercita aquí es la MISMA que se capturó del CLI real antes de escribir
/// una línea: <c>initialize</c> → <c>notifications/initialized</c> → <c>tools/list</c> →
/// <c>tools/call</c>. Trabajar sobre streams en vez de sobre la consola es lo que permite probarlo
/// entero sin suscripción de nadie.
/// </para>
/// </summary>
public sealed class McpServerTests
{
    // ---------------------------------------------------------------- handshake y catálogo

    [Fact]
    public void El_handshake_devuelve_la_version_que_propone_el_cliente()
    {
        AtalayaMcpServer server = Audit(new RecordingToolbox());

        JsonNode? response = server.Handle(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");

        response!["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-11-25",
            "negociar es devolver la que propone el cliente, no imponer la nuestra");
        response["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("atalaya");
        response["result"]!["capabilities"]!["tools"].Should().NotBeNull();
    }

    /// <summary>
    /// Atalaya NO ofrece recursos ni prompts: el código viaja en el prompt de la sesión, y darle al
    /// auditor un canal para pedir ficheros sería exactamente la superficie que no debe tener.
    /// </summary>
    [Fact]
    public void El_servidor_no_ofrece_ni_recursos_ni_prompts()
    {
        AtalayaMcpServer server = Audit(new RecordingToolbox());

        JsonNode capabilities = server.Handle(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{}}""")!["result"]!["capabilities"]!;

        capabilities.AsObject().Should().ContainSingle().Which.Key.Should().Be("tools");
    }

    /// <summary>
    /// Las tools que ve Claude Code son las MISMAS que las de Copilot. Si esta lista se separa de
    /// la de <c>RealCopilotAgent</c>, el mismo prompt significa dos cosas distintas según quién lo
    /// lea y las dos casas dejan de ser comparables.
    /// </summary>
    [Fact]
    public void El_catalogo_de_auditoria_es_el_mismo_vocabulario_que_ve_Copilot()
    {
        AtalayaMcpServer server = Audit(new RecordingToolbox());

        JsonArray tools = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")
            !["result"]!["tools"]!.AsArray();

        tools.Select(t => t!["name"]!.GetValue<string>()).Should().BeEquivalentTo(
            "submit_findings", "submit_finding", "report_verdicts",
            "add_locations", "unit_done", "read_signatures");

        // Y cada una llega con su esquema: sin él el modelo no sabe qué mandar.
        tools.Should().OnlyContain(t => t!["inputSchema"] != null && t["description"] != null);
    }

    [Fact]
    public void El_catalogo_de_verificacion_tiene_su_unica_tool()
    {
        var server = new AtalayaMcpServer(AuditorTools.ForVerify(new RecordingToolbox()));

        JsonArray tools = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")
            !["result"]!["tools"]!.AsArray();

        tools.Select(t => t!["name"]!.GetValue<string>()).Should().BeEquivalentTo("submit_verdict");
    }

    /// <summary>
    /// El nombre cualificado que el CLI le enseña al modelo, y que hay que pasarle a
    /// <c>--allowedTools</c>. Se comprobó contra el CLI real: <c>mcp__atalaya__submit_finding</c>.
    /// </summary>
    [Fact]
    public void El_nombre_cualificado_es_el_que_espera_el_CLI()
        => AuditorTools.Qualified("submit_finding").Should().Be("mcp__atalaya__submit_finding");

    // ---------------------------------------------------------------- llamadas

    [Fact]
    public void Una_llamada_llega_al_toolbox_con_sus_argumentos()
    {
        var toolbox = new RecordingToolbox();
        AtalayaMcpServer server = Audit(toolbox);

        JsonNode? response = server.Handle("""
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"submit_finding",
             "arguments":{"ruleId":"nulos","pillar":"Robustez","severity":"alta","title":"Divide sin comprobar cero",
             "description":"d","impact":"i","recommendation":"r","symbol":"Div",
             "locations":[{"path":"Calc.cs","line":12,"snippet":"a / b"}]}}}
            """);

        toolbox.Findings.Should().ContainSingle();
        SubmitFindingArgs finding = toolbox.Findings[0];
        finding.RuleId.Should().Be("nulos");
        finding.Severity.Should().Be("alta");
        finding.Symbol.Should().Be("Div");
        finding.Locations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SubmitLocation("Calc.cs", 12, "a / b"));

        response!["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
        server.ToolCalls.Should().Be(1);
    }

    [Fact]
    public void Los_veredictos_llegan_en_bloque_y_en_orden()
    {
        var toolbox = new RecordingToolbox();
        AtalayaMcpServer server = Audit(toolbox);

        server.Handle("""
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"report_verdicts",
             "arguments":{"verdicts":[
               {"findingId":"01AAA","verdict":"presente","evidence":"sigue ahi"},
               {"findingId":"01BBB","verdict":"arreglado","evidence":"ahora comprueba cero"}]}}}
            """);

        toolbox.Verdicts.Select(v => (v.FindingId, v.Verdict))
            .Should().Equal(("01AAA", "presente"), ("01BBB", "arreglado"));
    }

    [Fact]
    public void Unit_done_transporta_lo_que_el_auditor_declara_haberse_callado()
    {
        var toolbox = new RecordingToolbox();
        AtalayaMcpServer server = Audit(toolbox);

        server.Handle("""
            {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"unit_done",
             "arguments":{"unitPath":"Calc.cs","summary":"1 hallazgo",
             "suppressedByPattern":[{"patternId":"P-2","count":3}]}}}
            """);

        toolbox.UnitPath.Should().Be("Calc.cs");
        toolbox.Suppressed.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SuppressedByPatternArgs("P-2", 3));
    }

    /// <summary>
    /// Un modelo manda a veces un número donde se pidió texto, o al revés. Eso no puede tumbar la
    /// unidad: se lee con tolerancia y quien decide si el payload vale es el toolbox, con su error
    /// tipado, que es quien conoce las reglas.
    /// </summary>
    [Fact]
    public void Los_tipos_flojos_del_modelo_no_tumban_la_unidad()
    {
        var toolbox = new RecordingToolbox();
        AtalayaMcpServer server = Audit(toolbox);

        JsonNode? response = server.Handle("""
            {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"submit_finding",
             "arguments":{"ruleId":"nulos","pillar":"Robustez","severity":"alta","title":"t",
             "description":"d","impact":"i","recommendation":"r",
             "locations":[{"path":"Calc.cs","line":"12"}]}}}
            """);

        response!["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
        toolbox.Findings[0].Locations[0].Line.Should().Be(12, "«12» y 12 son la misma línea");
    }

    // ---------------------------------------------------------------- errores que NO tiran la conexión

    /// <summary>
    /// Que la aplicación rechace un payload es NORMAL —un ULID que no está en la lista, una
    /// severidad inventada—. Tiene que llegarle al modelo como resultado para que se corrija, no
    /// como una excepción que se lleve por delante la tubería y con ella la unidad entera.
    /// </summary>
    [Fact]
    public void Una_tool_que_revienta_se_contesta_al_modelo_en_vez_de_romper_la_tuberia()
    {
        AtalayaMcpServer server = Audit(new ExplodingToolbox());

        JsonNode? response = server.Handle("""
            {"jsonrpc":"2.0","id":6,"method":"tools/call",
             "params":{"name":"unit_done","arguments":{"unitPath":"A.cs","summary":"s"}}}
            """);

        response!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        response["result"]!["content"]![0]!["text"]!.GetValue<string>()
            .Should().Contain("ULID desconocido");
        response["error"].Should().BeNull("es un error DE LA TOOL, no del protocolo");
    }

    [Fact]
    public void Una_tool_que_no_existe_se_contesta_y_el_modelo_puede_corregirse()
    {
        AtalayaMcpServer server = Audit(new RecordingToolbox());

        JsonNode? response = server.Handle("""
            {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"rm_rf","arguments":{}}}
            """);

        response!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        server.ToolCalls.Should().Be(0, "lo que no existe no se cuenta como llamada atendida");
    }

    [Fact]
    public void Una_linea_ilegible_se_contesta_con_error_de_parseo_y_no_corta_la_sesion()
    {
        AtalayaMcpServer server = Audit(new RecordingToolbox());

        JsonNode? response = server.Handle("{esto no es json");

        response!["error"]!["code"]!.GetValue<int>().Should().Be(-32700);
    }

    [Fact]
    public void Un_metodo_desconocido_no_es_motivo_para_cerrar()
    {
        AtalayaMcpServer server = Audit(new RecordingToolbox());

        JsonNode? response = server.Handle("""{"jsonrpc":"2.0","id":8,"method":"resources/list"}""");

        response!["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
    }

    /// <summary>
    /// Una notificación no lleva id y NO se contesta nunca. Contestarla deja a algunos clientes
    /// esperando un turno que ya habían dado por cerrado.
    /// </summary>
    [Fact]
    public void Una_notificacion_no_se_contesta()
        => Audit(new RecordingToolbox())
            .Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")
            .Should().BeNull();

    // ---------------------------------------------------------------- el bucle entero

    /// <summary>
    /// La conversación completa sobre streams, que es como corre de verdad: handshake, catálogo,
    /// dos llamadas y cierre limpio cuando el cliente cierra su lado.
    /// </summary>
    [Fact]
    public async Task La_conversacion_entera_corre_sobre_streams_y_termina_al_cerrarse_la_entrada()
    {
        var toolbox = new RecordingToolbox();
        AtalayaMcpServer server = Audit(toolbox);

        string script = string.Join('\n',
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"unit_done","arguments":{"unitPath":"A.cs","summary":"limpia"}}}""") + "\n";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(script));
        using var output = new MemoryStream();

        await server.ServeAsync(input, output, CancellationToken.None);

        string[] replies = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        replies.Should().HaveCount(3, "la notificación no se contesta");
        toolbox.UnitPath.Should().Be("A.cs");
    }

    // ---------------------------------------------------------------- ayudas

    private static AtalayaMcpServer Audit(IAuditToolbox toolbox)
        => new(AuditorTools.ForAudit(toolbox));

    private sealed class RecordingToolbox : IAuditToolbox, IVerifyToolbox
    {
        public List<SubmitFindingArgs> Findings { get; } = new();

        public List<VerdictArgs> Verdicts { get; } = new();

        public SuppressedByPatternArgs[]? Suppressed { get; private set; }

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
        {
            Verdicts.AddRange(verdicts);
            return new ReportVerdictsResult(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());
        }

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
            => new(true, locations.Length);

        public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
        {
            UnitPath = unitPath;
            Suppressed = suppressedByPattern;
        }

        public string ReadSignatures(string path) => "// firmas";

        public void SubmitVerdict(string findingUlid, string verdict, string evidence)
        {
        }
    }

    /// <summary>El toolbox que rechaza, que es lo que hace el de verdad con un payload inválido.</summary>
    private sealed class ExplodingToolbox : IAuditToolbox
    {
        public SubmitFindingResult SubmitFinding(SubmitFindingArgs args) => throw new InvalidOperationException("ULID desconocido");

        public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings) => throw new InvalidOperationException("ULID desconocido");

        public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts) => throw new InvalidOperationException("ULID desconocido");

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations) => throw new InvalidOperationException("ULID desconocido");

        public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
            => throw new InvalidOperationException("ULID desconocido");

        public string ReadSignatures(string path) => throw new InvalidOperationException("ULID desconocido");
    }
}
