using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.OpenAI.Tests;

/// <summary>
/// <b>El bucle de herramientas y el consumo</b> (PROV-3 §4 y §6).
/// <para>
/// <b>Todo contra un doble en memoria, y es lo correcto.</b> Esta capa habla por
/// <see cref="IChatEndpoint"/> y no sabe una palabra de HTTP: probarla con un servidor falso
/// probaría el transporte, que es de otro, y además ataría estos tests a decisiones —cabeceras,
/// SSE, reintentos— que no tienen nada que ver con lo que se afirma aquí.
/// </para>
/// </summary>
public sealed class BucleTests
{
    // =========================================================== (4) el catálogo

    [Fact]
    public void Las_siete_se_ofrecen_con_el_nombre_y_la_descripcion_del_catalogo_compartido()
    {
        // REGLA: el texto que lee el modelo sale de AuditToolText, la misma constante que leen
        // Copilot y Claude Code. Aquí solo se traduce de forma.
        //
        // EN SILENCIO: si una descripción se reescribiera —o se recortara— el prompt de unidad,
        // que es el MISMO para las tres casas, significaría tres cosas distintas. No falla nada:
        // simplemente una discrepancia entre proveedores deja de poder atribuirse al modelo, que
        // es toda la razón de tener un segundo auditor. Ya pasó una vez, con `unit_done`.
        var toolbox = new ToolboxFalso();

        IReadOnlyList<ChatFunction> audit = AuditorFunctions.ForAudit(toolbox);
        IReadOnlyList<ChatFunction> verify = AuditorFunctions.ForVerify(toolbox);

        audit.Select(f => f.Name).Should().Equal(AuditToolText.Audit, "el orden del catálogo es el del prompt (D-883)");
        verify.Select(f => f.Name).Should().Equal(AuditToolText.Verify);

        foreach (ChatFunction f in audit.Concat(verify))
        {
            string suyo = AuditToolText.DescriptionOf(f.Name);
            suyo.Should().NotBeEmpty();

            // Palabra por palabra la del catálogo. Lo ÚNICO que este transporte añade es la
            // terminalidad, que `functions` no sabe declarar de otra forma que con palabras.
            string esperada = f.IsTerminal ? suyo + AuditorFunctions.TerminalSuffix : suyo;
            f.Spec.Description.Should().Be(esperada);
        }

        audit.Where(f => f.IsTerminal).Select(f => f.Name)
            .Should().Equal(new[] { AuditToolText.UnitDone }, "la marca sale del catálogo, no de un true escrito a mano");
        verify.Should().OnlyContain(f => !f.IsTerminal, "una verificación no cierra ninguna unidad");
    }

    // =========================================================== (4) el bucle

    [Fact]
    public async Task Una_llamada_se_ejecuta_en_el_toolbox_y_su_veredicto_vuelve_como_mensaje_tool()
    {
        // REGLA: el proveedor NO juzga. Ejecutar una herramienta es llamar al IAuditToolbox de la
        // aplicación —el que trae las 21 guardas de dominio— y devolverle al modelo exactamente lo
        // que ese toolbox conteste, rechazo incluido, en un mensaje `tool` colgado de su llamada.
        //
        // EN SILENCIO: un bucle que validara por su cuenta, o que se tragara el rechazo y siguiera,
        // daría por bueno un hallazgo que la aplicación rechaza. No hay excepción, no hay log rojo:
        // el informe sale con un hallazgo que nunca se guardó, o sin uno que sí. Y si el mensaje
        // `tool` no colgara del `assistant` que lo pidió, el dialecto queda mal formado y un
        // endpoint de verdad contesta 400 — que aquí no se vería nunca.
        var toolbox = new ToolboxFalso
        {
            Juicio = _ => new SubmitFindingResult(false, Error: "ese ULID no es de esta unidad"),
        };

        var endpoint = new EndpointGuionizado()
            .Turno(Herramienta("call_1", AuditToolText.SubmitFinding, """{"ruleId":"R-1","title":"eso"}"""))
            .Turno(Terminal("call_2"));

        await Proveedor(endpoint).AuditUnitAsync(Peticion(), toolbox, CancellationToken.None);

        // Llegó al toolbox, con lo que el modelo escribió y sin que nadie lo tocara por el camino.
        toolbox.Hallazgos.Should().ContainSingle();
        toolbox.Hallazgos[0].RuleId.Should().Be("R-1");
        toolbox.Hallazgos[0].Title.Should().Be("eso");

        // Y el veredicto del toolbox vuelve al modelo para que se corrija.
        IReadOnlyList<ChatMessage> segunda = endpoint.Conversaciones[1];
        segunda.Select(m => m.Role).Should().Equal("user", "assistant", "tool");
        segunda[1].ToolCalls.Should().ContainSingle().Which.Id.Should().Be("call_1");
        segunda[2].ToolCallId.Should().Be("call_1");
        segunda[2].Content.Should().Contain("ese ULID no es de esta unidad");
    }

    [Fact]
    public async Task unit_done_cierra_la_pasada_y_no_se_pide_otra_vuelta()
    {
        // REGLA: la terminal del catálogo corta. Se ejecuta, se cierra el turno y NO se vuelve a
        // preguntar.
        //
        // EN SILENCIO: sin el corte el bucle seguiría preguntando hasta el techo. Once llamadas de
        // más por unidad, cada una reenviando el prompt entero —que es lo que de verdad multiplica
        // el coste (D-866)— y ni un error en ningún sitio: solo una factura que no cuadra con lo
        // que se hizo.
        var toolbox = new ToolboxFalso();
        var endpoint = new EndpointGuionizado { PorDefecto = Terminal("call_1") };

        await Proveedor(endpoint).AuditUnitAsync(Peticion(), toolbox, CancellationToken.None);

        endpoint.Llamadas.Should().Be(1, "unit_done cierra la pasada en la vuelta en que llega");
        toolbox.UnidadCerrada.Should().Be("Unidad.cs", "y se ejecuta antes de cerrar, no en su lugar");
    }

    [Fact]
    public async Task El_bucle_no_puede_girar_sin_fin_y_dice_por_que_paro()
    {
        // REGLA: hay un techo de vueltas y, al llegar, la pasada se cierra diciendo el motivo por
        // CutSkipped (ICuttingAuditor). El techo vive en dominio —AuditLoopLimits— y nunca es cero.
        //
        // EN SILENCIO: un modelo que nunca llame a unit_done —porque el prompt no le cupo, porque
        // se atascó pidiendo firmas— dejaría este `while` girando. No es un cuelgue visible: es
        // una sesión que sigue «trabajando» y facturando llamada tras llamada. Y el tope del
        // coordinador (MaxCallsPerPass) no cubre esto: se puede desactivar con 0, no existe en
        // VerifyCoordinator, y solo cuenta si el endpoint publica `usage`.
        var toolbox = new ToolboxFalso();
        var endpoint = new EndpointGuionizado
        {
            PorDefecto = Herramienta("call_x", AuditToolText.ReadSignatures, """{"path":"Otro.cs"}"""),
        };

        var proveedor = Proveedor(endpoint, maxTurns: 4);
        var motivos = new List<string>();
        proveedor.CutSkipped += motivos.Add;

        await proveedor.AuditUnitAsync(Peticion(), toolbox, CancellationToken.None);

        endpoint.Llamadas.Should().Be(4, "el techo es el de la aplicación, no uno inventado aquí");
        motivos.Should().ContainSingle().Which.Should().Contain("4").And.Contain(AuditToolText.UnitDone);
    }

    [Fact]
    public void El_techo_de_vueltas_nunca_es_cero()
    {
        // REGLA: AuditLoopLimits.MaxTurns cae al de dominio cuando no hay ajuste o está desactivado.
        //
        // EN SILENCIO: `MaxCallsPerPass = 0` significa «desactivado» en el coordinador y allí es
        // legítimo —queda el techo de tokens como red—. Si ese 0 se copiara tal cual a este bucle
        // sería un `while` sin salida, y nadie lo notaría hasta ver la factura.
        AuditLoopLimits.MaxTurns(null).Should().Be(AuditLoopLimits.DefaultMaxTurns);
        AuditLoopLimits.MaxTurns(0).Should().Be(AuditLoopLimits.DefaultMaxTurns);
        AuditLoopLimits.MaxTurns(-1).Should().Be(AuditLoopLimits.DefaultMaxTurns);
        AuditLoopLimits.MaxTurns(3).Should().Be(3, "quien sepa leer el ajuste manda");
    }

    // =========================================================== (6) el coste

    [Fact]
    public async Task El_consumo_llega_como_UsageSample_con_la_cache_leida_en_su_sitio()
    {
        // REGLA: prompt_tokens → entrada, completion_tokens → salida,
        // prompt_tokens_details.cached_tokens → caché LEÍDA, y caché escrita 0 porque en este
        // dialecto no existe. Una muestra por llamada, con el modelo puesto y sin importe.
        //
        // EN SILENCIO: este proveedor declara InputIncludesCache, así que el cálculo resta la
        // caché leída de la entrada antes de tarifarla (CostCalculator). Poner los cacheados en el
        // campo equivocado —o no ponerlos— no produce ningún error: produce un coste distinto del
        // real en todas las sesiones de esta casa, y nadie tiene con qué compararlo.
        var toolbox = new ToolboxFalso();
        var endpoint = new EndpointGuionizado()
            .Turno(new ChatTurn("hola", Array.Empty<ChatToolCall>(), new ChatUsage(1200, 80, 900), "stop"));

        var proveedor = Proveedor(endpoint);
        var muestras = new List<UsageSample>();
        proveedor.UsageReported += muestras.Add;

        await proveedor.AuditUnitAsync(Peticion(), toolbox, CancellationToken.None);

        UsageSample u = muestras.Should().ContainSingle().Subject;
        u.InputTokens.Should().Be(1200, "la entrada de este dialecto INCLUYE lo cacheado");
        u.OutputTokens.Should().Be(80);
        u.CacheReadTokens.Should().Be(900);
        u.CacheWriteTokens.Should().Be(0, "chat/completions no tiene caché escrita: cero es la verdad, no un hueco");
        u.Model.Should().Be("un-modelo", "sin modelo no hay tarifa que buscar");
        u.Cost.Should().BeNull("el importe lo deriva la aplicación de los tokens y la tarifa, no lo dice el endpoint");
        u.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Una_vuelta_sin_usage_se_cuenta_igual_como_una_llamada()
    {
        // REGLA: se publica una muestra por llamada aunque el endpoint no mande `usage`, con los
        // tokens a cero —que es «no lo dijo»— y Calls = 1.
        //
        // EN SILENCIO: `usage` es opcional al hacer streaming en este dialecto y varias casas que
        // lo hablan no lo mandan. Si no se publicara nada, el contador de llamadas de la sesión se
        // quedaría a cero y con él el techo del coordinador (MaxCallsPerPass), que cuenta por las
        // muestras: la única red exterior de este bucle dejaría de existir sin que nada lo diga.
        var toolbox = new ToolboxFalso();
        var endpoint = new EndpointGuionizado()
            .Turno(new ChatTurn(string.Empty, new[] { Llamada("call_1", AuditToolText.ReadSignatures, """{"path":"a.cs"}""") }, null, "tool_calls"))
            .Turno(Terminal("call_2"));

        var proveedor = Proveedor(endpoint);
        var muestras = new List<UsageSample>();
        proveedor.UsageReported += muestras.Add;

        await proveedor.AuditUnitAsync(Peticion(), toolbox, CancellationToken.None);

        muestras.Should().HaveCount(2, "dos llamadas al endpoint son dos llamadas, las cuente él o no");
        muestras.Sum(m => m.Calls).Should().Be(2);
        muestras[0].InputTokens.Should().Be(0);
    }

    // =========================================================== andamiaje de estos tests

    private static OpenAiCompatibleProvider Proveedor(IChatEndpoint chat, int? maxTurns = null)
        => new(
            () => new OpenAiEndpoint("https://api.ejemplo.com/v1", "un-modelo"),
            () => "una-clave",
            chat: () => chat,
            maxTurns: () => maxTurns);

    private static AuditUnitRequest Peticion()
        => new(
            "Unidad.cs",
            "// código",
            "El prompt de la unidad.",
            TechStack.DotNet,
            AuditMode.Lotes,
            Array.Empty<ExistingFinding>());

    private static ChatToolCall Llamada(string id, string tool, string args) => new(id, tool, args);

    private static ChatTurn Herramienta(string id, string tool, string args)
        => new(string.Empty, new[] { Llamada(id, tool, args) }, new ChatUsage(100, 10), "tool_calls");

    private static ChatTurn Terminal(string id)
        => Herramienta(id, AuditToolText.UnitDone, """{"unitPath":"Unidad.cs","summary":"hecho"}""");
}

/// <summary>
/// <b>Un endpoint guionizado, en memoria</b>: se le dan los turnos que va a contestar y apunta las
/// conversaciones que recibe. No hay red, no hay SSE y no hay nada del transporte — que es
/// exactamente lo que hace que estos tests hablen del bucle y de nada más.
/// </summary>
internal sealed class EndpointGuionizado : IChatEndpoint
{
    private readonly Queue<ChatTurn> _turnos = new();

    /// <summary>Cada conversación tal y como se mandó. Copiada: el bucle muta la suya.</summary>
    public List<IReadOnlyList<ChatMessage>> Conversaciones { get; } = new();

    /// <summary>Las herramientas ofrecidas en cada vuelta.</summary>
    public List<IReadOnlyList<ChatToolSpec>> Ofrecidas { get; } = new();

    public int Llamadas => Conversaciones.Count;

    /// <summary>Lo que contesta cuando se acaba el guion. El test del techo vive de esto.</summary>
    public ChatTurn? PorDefecto { get; set; }

    public EndpointGuionizado Turno(ChatTurn turno)
    {
        _turnos.Enqueue(turno);
        return this;
    }

    public Task<ChatTurn> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolSpec> tools,
        Action<string>? onText,
        CancellationToken ct)
    {
        Conversaciones.Add(messages.ToList());
        Ofrecidas.Add(tools);

        if (_turnos.Count > 0)
        {
            return Task.FromResult(_turnos.Dequeue());
        }

        return PorDefecto is not null
            ? Task.FromResult(PorDefecto)
            : throw new InvalidOperationException(
                $"El endpoint guionizado se quedó sin turnos en la llamada {Llamadas}. "
                + "O el bucle llama más veces de las que el test esperaba, o al test le falta una.");
    }

    public Task<ChatProbe> ProbeAsync(CancellationToken ct)
        => throw new NotSupportedException("«Probar» no es del bucle.");
}

/// <summary>
/// El toolbox de la aplicación, en pequeño: apunta lo que le llega y contesta lo que el test le
/// diga. <b>No imita ninguna guarda</b> —las de verdad viven en <c>SessionToolbox</c> y este bucle
/// no las conoce—; lo que se afirma con él es que la llamada le llega entera y que su veredicto
/// vuelve al modelo sin que nadie lo toque.
/// </summary>
internal sealed class ToolboxFalso : IAuditToolbox, IVerifyToolbox
{
    public List<SubmitFindingArgs> Hallazgos { get; } = new();

    public List<VerdictArgs> Veredictos { get; } = new();

    public string? UnidadCerrada { get; private set; }

    /// <summary>Qué contesta a un <c>submit</c>. Por defecto, aceptado.</summary>
    public Func<SubmitFindingArgs, SubmitFindingResult>? Juicio { get; set; }

    public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
    {
        Hallazgos.Add(args);
        return Juicio?.Invoke(args) ?? new SubmitFindingResult(true);
    }

    public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
        => new(findings.Select(SubmitFinding).ToList());

    public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
    {
        Veredictos.AddRange(verdicts);
        return new ReportVerdictsResult(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());
    }

    public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
        => new(true, locations.Length);

    public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
        => UnidadCerrada = unitPath;

    public string ReadSignatures(string path) => $"// firmas de {path}";

    public void SubmitVerdict(string findingUlid, string verdict, string evidence)
        => Veredictos.Add(new VerdictArgs(findingUlid, verdict, evidence));
}
