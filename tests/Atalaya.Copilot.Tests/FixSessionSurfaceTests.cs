using Atalaya.Copilot;
using FluentAssertions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F6.9 §3 — LA SUPERFICIE QUE SE LE DA AL AGENTE, como invariante.
/// <para>
/// Un agente que edita el clon del usuario no puede tener más de lo que se le da a propósito. Aquí
/// se comprueba lo que la sesión de arreglo pone sobre la mesa —cuatro tools y ni una más—, que el
/// <c>OnPermissionRequest</c> rechaza TODO lo demás (shell, escritura directa, lectura, MCP, red),
/// y que la tool <c>ask_user</c> del runtime tiene a quién preguntar.
/// <para>
/// Se prueba sobre la <c>SessionConfig</c> y no sobre una sesión real porque una salvaguarda que
/// solo se puede comprobar teniendo un asiento de Copilot delante no se comprueba nunca — que es
/// exactamente lo que pasó con el mapeo de eventos hasta que se hizo verificable (H5).
/// </para>
/// </summary>
public sealed class FixSessionSurfaceTests
{
    private static SessionConfig Config(RecordingToolbox? toolbox = null, RecordingQuestions? questions = null)
        => new RealCopilotAgent().BuildFixSessionConfig(
            new FixRequest("prompt", @"C:\clon"),
            new FixConversation(toolbox ?? new RecordingToolbox(), questions ?? new RecordingQuestions()),
            CancellationToken.None);

    [Fact]
    public void La_sesion_de_arreglo_expone_exactamente_cuatro_tools()
    {
        var names = Config().Tools!.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        names.Should().Equal("apply_edit", "fix_done", "read_file", "run_build_and_tests");
    }

    /// <summary>
    /// Lo que NO está es lo que importa: el agente no tiene forma de pedir una shell, un commit,
    /// un push ni una descarga. Se nombran a propósito los que dolerían.
    /// </summary>
    [Theory]
    [InlineData("shell")]
    [InlineData("bash")]
    [InlineData("git")]
    [InlineData("commit")]
    [InlineData("push")]
    [InlineData("write_file")]
    [InlineData("fetch")]
    public void No_hay_ninguna_tool_de_shell_de_git_ni_de_red(string forbidden)
        => Config().Tools!.Select(t => t.Name)
            .Should().NotContain(n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// El permission handler DENIEGA todo lo que no sean nuestras tools. Las nuestras van con
    /// <c>SkipPermission</c>, así que ni siquiera llegan aquí: lo que llega es lo que el runtime
    /// intentaría por su cuenta, y todo eso se rechaza.
    /// </summary>
    [Fact]
    public async Task El_permission_handler_deniega_TODO_lo_que_no_sean_nuestras_tools()
    {
        var handler = Config().OnPermissionRequest;
        handler.Should().NotBeNull("sin handler el runtime decidiría por su cuenta");

        // Se recorren TODAS las clases de petición que el SDK define, no una lista escrita a
        // mano: si una versión futura añade una forma nueva de pedir permiso, este test la coge
        // sola. Las instancias se crean sin inicializar porque el handler no las mira — rechaza
        // por principio, que es justamente lo que se quiere fijar.
        var kinds = typeof(PermissionRequest).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(PermissionRequest)) && !t.IsAbstract)
            .ToList();

        kinds.Should().HaveCountGreaterThan(4, "el SDK define varias formas de pedir permiso");

        foreach (Type type in kinds)
        {
            var request = (PermissionRequest)System.Runtime.CompilerServices
                .RuntimeHelpers.GetUninitializedObject(type);
            PermissionDecision decision = await handler!(request, null!);
            decision.Should().NotBeNull();
            Kind(decision).Should().Be("reject",
                $"una petición de tipo {type.Name} tiene que quedar rechazada");
            Kind(decision).Should().NotContain("allow").And.NotContain("approve");
        }
    }

    /// <summary>El agente vive dentro del clon: es su directorio de trabajo, no otro.</summary>
    [Fact]
    public void La_sesion_trabaja_dentro_del_clon()
        => Config().WorkingDirectory.Should().Be(@"C:\clon");

    /// <summary>
    /// <c>ask_user</c> tiene a quién preguntar. Verificado contra el SDK 1.0.11: la tool del
    /// runtime desemboca en <c>SessionConfig.OnUserInputRequest</c>, y sin handler la elicitación
    /// —la mitad del producto— no existiría.
    /// </summary>
    [Fact]
    public async Task La_pregunta_del_agente_llega_a_la_aplicacion_y_la_respuesta_vuelve()
    {
        var questions = new RecordingQuestions("(B) compatible + aviso");
        var handler = Config(questions: questions).OnUserInputRequest;
        handler.Should().NotBeNull();

        UserInputResponse response = await handler!(
            new UserInputRequest
            {
                Question = "¿Lanzo excepción o mantengo compatibilidad?",
                Choices = new List<string> { "(A) excepción", "(B) compatible + aviso" },
                AllowFreeform = true,
            },
            new UserInputInvocation());

        questions.Asked.Should().ContainSingle().Which.Should().Contain("excepción");
        questions.Choices.Should().HaveCount(2);
        response.Answer.Should().Be("(B) compatible + aviso");
        response.WasFreeform.Should().BeFalse("la respuesta salió de la lista de opciones");
    }

    /// <summary>Una respuesta que el usuario escribió a mano se marca como tal.</summary>
    [Fact]
    public async Task Una_respuesta_libre_se_marca_como_libre()
    {
        var handler = Config(questions: new RecordingQuestions("prefiero TryParse")).OnUserInputRequest;

        UserInputResponse response = await handler!(
            new UserInputRequest { Question = "¿Cómo lo resuelvo?", Choices = new List<string> { "(A)" } },
            new UserInputInvocation());

        response.Answer.Should().Be("prefiero TryParse");
        response.WasFreeform.Should().BeTrue();
    }

    /// <summary>
    /// El agente falso implementa el arreglo; los dobles de test que solo auditan, no — y lo dicen
    /// lanzando en vez de fingir que arreglan.
    /// </summary>
    [Fact]
    public async Task Un_agente_que_no_sabe_arreglar_lo_dice()
    {
        ICopilotAgent agent = new AuditOnlyAgent();

        Func<Task> act = () => agent.FixAsync(
            new FixRequest("p", "c"),
            new FixConversation(new RecordingToolbox(), new RecordingQuestions()),
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static string Kind(PermissionDecision decision)
        => (decision.GetType().GetProperty("Kind")?.GetValue(decision) as string ?? string.Empty)
            .ToLowerInvariant();

    private sealed class RecordingToolbox : IFixToolbox
    {
        public ReadFileResult ReadFile(string path) => new(true, "x");

        public ApplyEditResult ApplyEdit(string path, string reason, FixEdit[] edits) => new(true);

        public BuildAndTestResult RunBuildAndTests() => new(true, "ok");

        public void FixDone(FixDoneArgs done)
        {
        }
    }

    private sealed class RecordingQuestions : IUserQuestions
    {
        private readonly string? _answer;

        public RecordingQuestions(string? answer = null) => _answer = answer;

        public List<string> Asked { get; } = new();

        public IReadOnlyList<string> Choices { get; private set; } = Array.Empty<string>();

        public Task<string?> AskAsync(
            string question, IReadOnlyList<string> choices, bool allowFreeform, CancellationToken ct)
        {
            Asked.Add(question);
            Choices = choices;
            return Task.FromResult(_answer);
        }
    }

    /// <summary>Un agente que solo audita: no sobrescribe <c>FixAsync</c>.</summary>
    private sealed class AuditOnlyAgent : ICopilotAgent
    {
        public string? ModelName => "audit-only";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "ok"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(0, 0, null, null));
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }
}
