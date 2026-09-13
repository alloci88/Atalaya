using System.Net;
using System.Text.Json;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.OpenAI.Tests;

/// <summary>
/// <b>Las reglas de PROV-3, contra el endpoint falso y sin tocar la red.</b>
/// <para>
/// Aquí no se prueba que el transporte parsee un <c>data:</c> ni que el bucle lleve la cuenta de
/// las pasadas —eso es de quien escribe cada frente y vive en <c>TransporteTests</c> y
/// <c>BucleTests</c>—: aquí se prueba lo que tiene que seguir siendo verdad <b>cuando los tres
/// frentes se junten</b>, que es lo único que ninguno de ellos puede comprobar solo.
/// </para>
/// <para>
/// <b>La regla de <c>http://</c> solo en local no está aquí</b>, y es a propósito: el andamio ya
/// la dejó cubierta en <c>AndamioTests.Solo_se_admite_http_en_local</c>, con las seis URLs que
/// importan. Repetirla sería un test por duplicado, que es justo lo que N-5 prohíbe.
/// </para>
/// </summary>
public sealed class ReglasTests
{
    // La clave de los tests tiene FORMA DE CLAVE DE VERDAD: `sk-` y cuarenta y ocho caracteres. Una
    // clave de mentira («clave») se colaría en un log sin que ningún test lo notara, porque nadie
    // la buscaría.
    private const string Clave = "sk-proj-7Qh2Vb9LmR4tXeZ1aK8sJ3nP6dW0yC5uG7iT2oA4fB9lE1rN";

    private const string Modelo = "modelo-de-pruebas";

    private static OpenAiEndpoint Config => new("https://api.ejemplo.invalid/v1", Modelo);

    // ============================================================ (2) el bucle de herramientas

    /// <summary>
    /// <b>Qué regla protege.</b> Una respuesta con <c>tool_calls</c> ejecuta la herramienta por el
    /// toolbox de la aplicación —con sus guardas, y lo que la guarda conteste es lo que vuelve al
    /// modelo—, manda el resultado como mensaje <c>tool</c> y sigue la conversación; y
    /// <c>unit_done</c> corta el turno.
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Un turno infinito —el modelo llama, la aplicación
    /// contesta, y nadie sabe quién para— que contra una API de pago se cobra cada vuelta; o una
    /// herramienta ejecutada por fuera del toolbox, que es lo mismo que ejecutarla sin guardas: el
    /// hallazgo se persiste aunque la ubicación caiga fuera de la unidad, y el modelo lee un «ok»
    /// que nadie ha dicho.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_llamada_de_herramienta_pasa_por_el_toolbox_y_unit_done_corta_el_turno()
    {
        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.SubmitFinding, ArgumentosDeHallazgo("src/A.cs")));
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.UnitDone,
            """{"unitPath":"src/A.cs","summary":"revisada"}"""));

        // La guarda RECHAZA. Es el caso que importa: lo que el modelo tiene que leer es la negativa
        // de la aplicación, no un acuse inventado por el transporte.
        var toolbox = new ToolboxEspia(
            new SubmitFindingResult(false, Error: "la ubicación cae fuera de la unidad"));

        OpenAiCompatibleProvider proveedor = Arnes.Proveedor(falso, Config, Clave);

        await proveedor.AuditUnitAsync(Peticion("src/A.cs"), toolbox, CancellationToken.None);

        toolbox.Hallazgos.Should().ContainSingle(
            "el hallazgo entra por el toolbox de la aplicación, que es quien tiene las guardas")
            .Which.RuleId.Should().Be("errores.recursos.no-liberado");

        toolbox.Cerrada.Should().Be("src/A.cs", "unit_done cierra la unidad por el toolbox");

        falso.Llamadas.Should().Be(2,
            "tras unit_done no hay un turno más: el corte es del bucle y no de un tope de cortesía");

        string segunda = falso.Recibidas[1].Cuerpo;
        Mensajes(segunda).Should().Contain(
            m => m.Rol == "tool" && m.Texto.Contains("fuera de la unidad", StringComparison.Ordinal),
            "lo que contestó la guarda vuelve al modelo como mensaje `tool`, tal cual");
    }

    // ============================================================ (4) el coste

    /// <summary>
    /// <b>Qué regla protege.</b> El <c>usage</c> del endpoint llega entero a la muestra de consumo
    /// —y <c>prompt_tokens_details.cached_tokens</c> llega a <b>caché leída</b>, no a la entrada—,
    /// el importe sale de la tarifa de ESE proveedor y ESE modelo, y un modelo sin tarifa deja el
    /// consumo sin valorar para que el agregado se marque parcial.
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Métricas mintiendo por proveedor: los cacheados
    /// contados como entrada fresca inflan la factura de todas las sesiones de esta casa, y si un
    /// modelo sin tarifa devolviera cero en vez de «no valorable», un gasto real se enseñaría como
    /// gratis y el aviso de parcial —que es lo que manda a arreglarlo— no saltaría nunca.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_consumo_del_endpoint_se_valora_con_su_tarifa_y_sin_tarifa_queda_por_valorar()
    {
        var falso = new EndpointFalso();
        falso.RespondeStream(TurnoQueCierraConConsumo(
            promptTokens: 1_000, completionTokens: 200, cachedTokens: 400));

        OpenAiCompatibleProvider proveedor = Arnes.Proveedor(falso, Config, Clave);

        UsageSample? muestra = null;
        proveedor.UsageReported += m => muestra = m;

        await proveedor.AuditUnitAsync(Peticion("src/A.cs"), new ToolboxEspia(), CancellationToken.None);

        muestra.Should().NotBeNull("sin muestra de consumo no hay coste que calcular");
        muestra!.InputTokens.Should().Be(1_000, "`prompt_tokens` es la entrada, cacheados incluidos");
        muestra.OutputTokens.Should().Be(200);
        muestra.CacheReadTokens.Should().Be(400, "`cached_tokens` es caché LEÍDA, no entrada fresca");
        muestra.CacheWriteTokens.Should().Be(0,
            "este dialecto no tiene caché escrita: no se inventa, se declara que no existe");
        muestra.Model.Should().Be(Modelo);

        // --- con tarifa: el importe es el de SU tarifa, y la caché leída se cobra a su precio.
        var tabla = new ModelRateTable();
        tabla.Rates.Add(new ModelRate(
            Modelo, OpenAiCompatibleProvider.Id,
            InputPerMillion: 10m, OutputPerMillion: 30m, CachedInputPerMillion: 1m));

        CostResult conTarifa = CostCalculator.Calculate(
            Modelo, OpenAiCompatibleProvider.Id,
            muestra.InputTokens, muestra.OutputTokens,
            muestra.CacheReadTokens, muestra.CacheWriteTokens,
            tabla, Rasgos);

        // 600 frescos × 10 $/M + 400 cacheados × 1 $/M + 200 de salida × 30 $/M.
        conTarifa.Usd.Should().Be(0.006m + 0.0004m + 0.006m,
            "la entrada facturable descuenta lo cacheado porque esta casa declara InputIncludesCache");

        // --- sin tarifa: un hueco de verdad, y sin frase que lo disculpe.
        CostResult sinTarifa = CostCalculator.Calculate(
            Modelo, OpenAiCompatibleProvider.Id,
            muestra.InputTokens, muestra.OutputTokens,
            muestra.CacheReadTokens, muestra.CacheWriteTokens,
            new ModelRateTable(), Rasgos);

        sinTarifa.HasValue.Should().BeFalse();
        sinTarifa.Why.Should().Be(CostUnavailable.RateMissing,
            "sin tarifa el agregado se marca PARCIAL; un cero diría que fue gratis");
        sinTarifa.IsUnpriced.Should().BeFalse(
            "esta casa no declara frase de «sin tarifa»: lo que falta es configuración, no una excusa");
    }

    // ============================================================ (5) la taxonomía

    /// <summary>
    /// <b>Qué regla protege.</b> Cada fallo del endpoint sale como el error compartido que le
    /// toca, con su mensaje para una persona; y el reintento es de uno y solo donde tiene sentido:
    /// <b>429 y 5xx sí, 401, 404 y el plantón no</b>.
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Un 401 reintentado en bucle contra una API de pago —o,
    /// al revés, un 429 que se rinde a la primera y tira media sesión—. Y un fallo sin clasificar
    /// se presenta como «error desconocido» donde había un remedio de un clic: cambiar el modelo,
    /// pegar la clave otra vez.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(401, nameof(AgentProblem.NotAuthenticated), 1)]
    [InlineData(404, nameof(AgentProblem.ModelUnavailable), 1)]
    [InlineData(429, nameof(AgentProblem.QuotaExhausted), 2)]
    [InlineData(500, "", 2)]
    [InlineData(0, nameof(AgentProblem.Offline), 1)]
    public async Task Cada_fallo_del_endpoint_da_su_error_compartido_y_solo_se_reintenta_lo_transitorio(
        int codigo, string problema, int llamadas)
    {
        // El 0 es el plantón: ni respuesta ni error del servidor, que es como se ve un timeout.
        var falso = new EndpointFalso();
        if (codigo == 0)
        {
            falso.RespondeTimeout().RespondeTimeout();
        }
        else
        {
            falso.RespondeError((HttpStatusCode)codigo).RespondeError((HttpStatusCode)codigo);
        }

        OpenAiCompatibleProvider proveedor = Arnes.Proveedor(falso, Config, Clave);

        Func<Task> correr = () => proveedor.AuditUnitAsync(
            Peticion("src/A.cs"), new ToolboxEspia(), CancellationToken.None);

        AuditorProviderException fallo = (await correr.Should().ThrowAsync<AuditorProviderException>(
                "el transporte traduce; encima nadie mira códigos HTTP"))
            .Which;

        fallo.Message.Should().NotBeNullOrWhiteSpace("el mensaje es para quien está delante");
        fallo.Message.Should().NotContain(Clave, "ni siquiera al fallar sale la clave");

        if (problema.Length > 0)
        {
            fallo.Problem.Should().Be(Enum.Parse<AgentProblem>(problema));
        }
        else
        {
            fallo.Problem.Should().NotBe(AgentProblem.None, "un 5xx tampoco se queda sin clasificar");
        }

        if (codigo == 401)
        {
            fallo.Should().BeOfType<AuditorAuthenticationException>(
                "la credencial tiene remedio propio y por eso tiene tipo propio");
        }

        if (codigo == 404)
        {
            fallo.Should().BeOfType<AuditorModelUnavailableException>(
                "el modelo que no existe se arregla en Ajustes, y el tipo es lo que ofrece ese enlace");
        }

        falso.Llamadas.Should().Be(llamadas,
            "se reintenta UNA vez y solo en 429 y 5xx: reintentar un 401 gasta contra una API de pago");
    }

    // ============================================================ (6) el prompt es el mismo

    /// <summary>
    /// <b>Qué regla protege.</b> Lo que sale hacia el endpoint lleva el prompt compartido
    /// —el que compone <c>PromptComposer</c>— <b>byte a byte</b>, sin recortes, sin envoltorio y
    /// sin una frase añadida por el camino.
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Una tercera versión del prompt. No falla nada: las
    /// sesiones corren, los hallazgos entran, y a partir de ese día esta casa juzga con reglas
    /// ligeramente distintas de las otras dos — así que comparar dos casas deja de significar lo
    /// que se cree que significa, y nadie lo nota porque no hay error que mirar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Los_mensajes_que_salen_llevan_el_prompt_compartido_byte_a_byte()
    {
        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta(
            AuditToolText.UnitDone, """{"unitPath":"src/A.cs","summary":"revisada"}"""));

        AuditUnitRequest peticion = Peticion("src/A.cs");
        OpenAiCompatibleProvider proveedor = Arnes.Proveedor(falso, Config, Clave);

        await proveedor.AuditUnitAsync(peticion, new ToolboxEspia(), CancellationToken.None);

        string enviado = string.Concat(Mensajes(falso.Recibidas[0].Cuerpo).Select(m => m.Texto));

        enviado.Should().Contain(peticion.Prompt, "el prompt viaja entero y sin tocar");
        Apariciones(enviado, peticion.Prompt).Should().Be(1,
            "entero y UNA vez: mandarlo dos veces cuesta el doble y no se nota en ningún sitio");
    }

    // ============================================================ lo de andar por casa

    /// <summary>Cómo cuenta sus tokens esta casa, tal y como el dominio lo necesita.</summary>
    private static ProviderCostTraits Rasgos => new(
        OpenAiCompatibleProvider.Id,
        TokenAccounting.InputIncludesCache,
        NoRateNote: null);

    /// <summary>Una unidad que auditar, con el prompt compartido de verdad dentro.</summary>
    private static AuditUnitRequest Peticion(string unitPath)
    {
        const string contenido = "class A { void M() { } }";
        ComposedUnitPrompt compuesto = PromptComposer.Compose(
            unitPath, contenido, PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes);

        return new AuditUnitRequest(
            unitPath, contenido, compuesto.Text, TechStack.DotNet, AuditMode.Lotes,
            Array.Empty<ExistingFinding>(),
            StablePrefix: compuesto.StablePrefix,
            UnitPart: compuesto.UnitPart);
    }

    private static string ArgumentosDeHallazgo(string path)
        => JsonSerializer.Serialize(new
        {
            ruleId = "errores.recursos.no-liberado",
            pillar = "errores",
            severity = "critica",
            title = "Conn leaked",
            description = "desc",
            impact = "impact",
            recommendation = "reco",
            locations = new[] { new { path, line = 1, snippet = "class A { void M() { } }" } },
            symbol = "A.M",
        });

    /// <summary>Un turno que cierra la unidad y declara el consumo, con su caché leída.</summary>
    private static string[] TurnoQueCierraConConsumo(
        long promptTokens, long completionTokens, long cachedTokens)
    {
        string[] herramienta = EndpointFalso.TurnoDeHerramienta(
            AuditToolText.UnitDone, """{"unitPath":"src/A.cs","summary":"revisada"}""");

        herramienta[^1] =
            "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],"
            + $"\"usage\":{{\"prompt_tokens\":{promptTokens},\"completion_tokens\":{completionTokens},"
            + $"\"prompt_tokens_details\":{{\"cached_tokens\":{cachedTokens}}}}}}}";

        return herramienta;
    }

    /// <summary>Los mensajes de un cuerpo de `chat/completions`, con su papel y su texto.</summary>
    private static IReadOnlyList<(string Rol, string Texto)> Mensajes(string cuerpo)
    {
        using JsonDocument doc = JsonDocument.Parse(cuerpo);
        if (!doc.RootElement.TryGetProperty("messages", out JsonElement mensajes))
        {
            return Array.Empty<(string, string)>();
        }

        var salida = new List<(string, string)>();
        foreach (JsonElement m in mensajes.EnumerateArray())
        {
            string rol = m.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? string.Empty : string.Empty;
            string texto = m.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;
            salida.Add((rol, texto));
        }

        return salida;
    }

    private static int Apariciones(string texto, string aguja)
    {
        if (aguja.Length == 0)
        {
            return 0;
        }

        int total = 0;
        for (int i = texto.IndexOf(aguja, StringComparison.Ordinal);
             i >= 0;
             i = texto.IndexOf(aguja, i + aguja.Length, StringComparison.Ordinal))
        {
            total++;
        }

        return total;
    }
}

/// <summary>
/// El toolbox de la aplicación, espiado: apunta lo que le llega y devuelve lo que el test le diga
/// que devuelva. Es el sustituto de <c>SessionToolbox</c> en los tests que no montan la aplicación
/// entera — lo que se comprueba aquí es que el bucle PASA POR ÉL, no lo que él decide.
/// </summary>
internal sealed class ToolboxEspia : IAuditToolbox
{
    private readonly SubmitFindingResult _respuesta;

    public ToolboxEspia(SubmitFindingResult? respuesta = null)
        => _respuesta = respuesta ?? new SubmitFindingResult(true);

    public List<SubmitFindingArgs> Hallazgos { get; } = new();

    public List<VerdictArgs> Veredictos { get; } = new();

    /// <summary>La unidad que se cerró, o null si nadie llamó a <c>unit_done</c>.</summary>
    public string? Cerrada { get; private set; }

    public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
    {
        Hallazgos.Add(args);
        return _respuesta;
    }

    public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
    {
        Hallazgos.AddRange(findings);
        return new SubmitFindingsResult(findings.Select(_ => _respuesta).ToArray());
    }

    public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
    {
        Veredictos.AddRange(verdicts);
        return new ReportVerdictsResult(verdicts.Select(_ => new ReportVerdictResult(true)).ToArray());
    }

    public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
        => new(true, locations.Length);

    public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
        => Cerrada = unitPath;

    public string ReadSignatures(string path) => string.Empty;
}
