using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// F16 §A — LA SUPERFICIE DE UNA SESIÓN DE ARREGLO CON CLAUDE CODE, como invariante.
/// <para>
/// Mismo papel que <c>FixSessionSurfaceTests</c> tiene del lado de Copilot: lo que se le da al
/// agente ES la salvaguarda entera de este flujo, y una salvaguarda que solo se puede comprobar
/// teniendo una suscripción delante no se comprueba nunca. Aquí se lee la lista de argumentos y el
/// catálogo de tools y se afirma sobre ellos.
/// </para>
/// </summary>
public sealed class FixSurfaceTests
{
    // ================================================================= el régimen de permisos

    /// <summary>
    /// <b>El régimen de permisos de Atalaya no se delega en el del CLI.</b> Las tres piezas van
    /// juntas y las tres importan: sin herramientas propias, con una lista cerrada de permitidas,
    /// y sin poder preguntar ni autorizar por su cuenta. Lo único que se puede permitir de más
    /// —tocar un fichero que no es del hallazgo— lo gobierna Atalaya dentro de <c>apply_edit</c>.
    /// </summary>
    [Fact]
    public void El_CLI_corre_sin_herramientas_propias_y_sin_poder_preguntar_ni_autorizar()
    {
        List<string> args = Arguments();

        Pair(args, "--tools").Should().BeEmpty("ni consola, ni ficheros, ni red");
        Pair(args, "--permission-mode").Should().Be("dontAsk", "ni pregunta él ni autoriza él");
        args.Should().Contain("--strict-mcp-config");
        Pair(args, "--setting-sources").Should().BeEmpty(
            "ni permisos ni hooks de la máquina de quien lanza");

        // Y nada que abra la puerta del todo. Se comprueba por lista negra ADEMÁS de por lista
        // blanca: un modo nuevo del CLI que otorgara permisos entraría por aquí sin avisar.
        args.Should().NotContain("--dangerously-skip-permissions");
        args.Should().NotContain("--allow-dangerously-skip-permissions");
        args.Should().NotContain("bypassPermissions");
        args.Should().NotContain("acceptEdits");
        args.Should().NotContain("--add-dir");
    }

    /// <summary>
    /// Y lo permitido es EXACTAMENTE el catálogo de la sesión: ni una tool de más, ni una de menos.
    /// </summary>
    [Fact]
    public void Solo_se_permiten_las_tools_de_Atalaya_y_son_exactamente_las_del_catalogo()
    {
        FixToolSet set = Set();
        List<string> args = Arguments(set);

        Pair(args, "--allowedTools").Split(',').Should().BeEquivalentTo(
            set.Tools.Select(t => $"mcp__atalaya__{t.Name}"));
        set.Tools.Select(t => t.Name).Should().BeEquivalentTo(FixToolText.All);
    }

    /// <summary>
    /// La conversación necesita entrada por líneas JSON; una auditoría, no. Lo segundo importa
    /// tanto como lo primero: una auditoría es de un solo turno y no tiene que quedarse esperando.
    /// </summary>
    [Fact]
    public void Solo_el_arreglo_abre_la_entrada_conversacional()
    {
        Arguments().Should().Contain("--input-format");
        Pair(Arguments(), "--input-format").Should().Be("stream-json");

        ClaudeCliRunner.BuildArguments(
            new ClaudeRun("audita", new[] { "mcp__atalaya__unit_done" }, "c.json", null))
            .Should().NotContain("--input-format");
    }

    // ================================================================= el catálogo

    /// <summary>
    /// <b>Las mismas cuatro que ve Copilot, palabra por palabra, y no por copia.</b> El texto sale
    /// de <see cref="FixToolText"/>, que comparten los dos drivers: con dos motores detrás del
    /// mismo contrato, una copia a mano es una copia esperando a divergir, y en cuanto divergiera
    /// una diferencia entre las dos casas dejaría de poder atribuirse al modelo.
    /// </summary>
    [Fact]
    public void El_catalogo_declara_las_cuatro_herramientas_con_el_texto_compartido()
    {
        JsonNode catalog = Catalog(Set());
        var byName = catalog["result"]!["tools"]!.AsArray()
            .ToDictionary(
                t => t!["name"]!.GetValue<string>(),
                t => t!["description"]!.GetValue<string>(),
                StringComparer.Ordinal);

        byName[FixToolText.ReadFile].Should().Be(FixToolText.ReadFileDescription);
        byName[FixToolText.ApplyEdit].Should().Be(FixToolText.ApplyEditDescription);
        byName[FixToolText.RunBuildAndTests].Should().Be(FixToolText.RunBuildAndTestsDescription);
        byName[FixToolText.FixDone].Should().Be(FixToolText.FixDoneDescription);
    }

    /// <summary>
    /// <c>run_build_and_tests</c> no admite argumentos, y eso es la salvaguarda entera (D-548): lo
    /// que se ejecuta lo decide Atalaya. Una tool que aceptara un comando sería una shell con otro
    /// nombre.
    /// </summary>
    [Fact]
    public void Compilar_no_admite_argumentos_asi_que_no_es_una_shell()
    {
        JsonNode catalog = Catalog(Set());
        JsonNode build = catalog["result"]!["tools"]!.AsArray()
            .First(t => t!["name"]!.GetValue<string>() == FixToolText.RunBuildAndTests)!;

        build["inputSchema"]!["properties"]!.AsObject().Should().BeEmpty();
        build["inputSchema"]!["required"].Should().BeNull();
    }

    // ================================================================= las llamadas

    /// <summary>
    /// <c>ask_user</c> va y vuelve: la pregunta llega a la aplicación con sus opciones y la
    /// respuesta del usuario vuelve como resultado de la tool. Es la mitad conversacional del
    /// producto, y aquí la sirve Atalaya porque el CLI corre sin herramientas propias.
    /// </summary>
    [Fact]
    public void La_pregunta_al_usuario_va_y_vuelve()
    {
        var questions = new ScriptedQuestions("(A) excepción");
        var server = new AtalayaMcpServer(
            FixTools.ForFix(new NullToolbox(), questions, CancellationToken.None).Tools);

        JsonNode? answer = server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ask_user","arguments":"""
            + """{"question":"¿(A) o (B)?","choices":["(A) excepción","(B) compatible"]}}}""");

        questions.Asked.Should().ContainSingle();
        questions.Asked[0].Question.Should().Be("¿(A) o (B)?");
        questions.Asked[0].Choices.Should().BeEquivalentTo("(A) excepción", "(B) compatible");
        // El texto viaja como JSON, así que se lee como JSON: comparar cadenas crudas haría que
        // este test dependiera de cómo escapa los acentos el serializador, que no es lo que prueba.
        JsonNode.Parse(Text(answer))!["answer"]!.GetValue<string>().Should().Be("(A) excepción");
    }

    /// <summary>
    /// Y una pregunta que nadie va a contestar —la sesión se detuvo— NO se contesta con una cadena
    /// vacía, que el modelo leería como «me da igual»: se le dice lo que ha pasado.
    /// </summary>
    [Fact]
    public void Una_pregunta_sin_respuesta_se_dice_en_vez_de_contestarse_en_blanco()
    {
        var server = new AtalayaMcpServer(
            FixTools.ForFix(new NullToolbox(), new ScriptedQuestions(null), CancellationToken.None).Tools);

        JsonNode? answer = server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ask_user","arguments":{"question":"¿?"}}}""");

        Text(answer).Should().Contain("se ha detenido");
    }

    /// <summary>
    /// <c>fix_done</c> es TERMINAL. En Copilot lo declara el SDK; aquí no hay quien lo declare —MCP
    /// no tiene el concepto—, así que lo mira el driver. Sin esto, la conversación seguiría
    /// pidiéndole al usuario qué decir después de que el arreglo estuviera cerrado.
    /// </summary>
    [Fact]
    public void Cerrar_el_arreglo_cierra_la_conversacion()
    {
        var toolbox = new NullToolbox();
        FixToolSet set = FixTools.ForFix(toolbox, new ScriptedQuestions("sí"), CancellationToken.None);
        var server = new AtalayaMcpServer(set.Tools);

        set.Closed.Should().BeFalse();

        server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"fix_done","arguments":"""
            + """{"summary":"s","commitTitle":"t","commitDescription":"d"}}}""");

        set.Closed.Should().BeTrue();
        toolbox.Done!.CommitTitle.Should().Be("t");
        toolbox.Done!.Risks.Should().BeNull("un campo opcional vacío es null, no una cadena vacía");
    }

    /// <summary>
    /// Un «no» del usuario vuelve como DECISIÓN y no como error de protocolo: el agente tiene que
    /// poder leerlo y replantear, no reintentar lo mismo (D-546).
    /// </summary>
    [Fact]
    public void Un_permiso_denegado_vuelve_como_decision()
    {
        var toolbox = new NullToolbox { EditResult = new ApplyEditResult(false, Denied: true) };
        var server = new AtalayaMcpServer(
            FixTools.ForFix(toolbox, new ScriptedQuestions("no"), CancellationToken.None).Tools);

        JsonNode? answer = server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"apply_edit","arguments":"""
            + """{"path":"otro.cs","reason":"porque sí","edits":[{"oldText":"a","newText":"b"}]}}}""");

        answer!["result"]!["isError"]!.GetValue<bool>().Should().BeFalse(
            "una decisión del usuario no es un fallo de la herramienta");
        Text(answer).Should().Contain("\"Denied\":true");
        toolbox.Edits.Should().ContainSingle().Which.Path.Should().Be("otro.cs");
    }

    // ================================================================= ayudas

    private static FixToolSet Set()
        => FixTools.ForFix(new NullToolbox(), new ScriptedQuestions("sí"), CancellationToken.None);

    private static List<string> Arguments(FixToolSet? set = null)
        => ClaudeCliRunner.BuildArguments(new ClaudeRun(
            "arregla esto",
            (set ?? Set()).Tools.Select(t => AuditorTools.Qualified(t.Name)).ToList(),
            "mcp.json",
            "opus",
            Conversational: true));

    private static string Pair(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        i.Should().BeGreaterThanOrEqualTo(0, $"{name} tiene que estar en la línea de órdenes");
        return args[i + 1];
    }

    private static JsonNode Catalog(FixToolSet set)
        => new AtalayaMcpServer(set.Tools)
            .Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;

    private static string Text(JsonNode? response)
        => response?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;

    /// <summary>Un toolbox que no toca nada: aquí se prueba el TRANSPORTE, no las reglas.</summary>
    private sealed class NullToolbox : IFixToolbox
    {
        public List<(string Path, string Reason)> Edits { get; } = new();

        public ApplyEditResult EditResult { get; init; } = new(true, Changed: 1);

        public FixDoneArgs? Done { get; private set; }

        public ReadFileResult ReadFile(string path) => new(true, "contenido", null, 29);

        public ApplyEditResult ApplyEdit(string path, string reason, FixEdit[] edits)
        {
            Edits.Add((path, reason));
            return EditResult;
        }

        public BuildAndTestResult RunBuildAndTests() => new(true, "verde");

        public void FixDone(FixDoneArgs done) => Done = done;
    }

    private sealed class ScriptedQuestions : IUserQuestions
    {
        private readonly string? _answer;

        public ScriptedQuestions(string? answer) => _answer = answer;

        public List<(string Question, IReadOnlyList<string> Choices, bool Free)> Asked { get; } = new();

        public Task<string?> AskAsync(
            string question, IReadOnlyList<string> choices, bool allowFreeform, CancellationToken ct)
        {
            Asked.Add((question, choices, allowFreeform));
            return Task.FromResult(_answer);
        }
    }
}
