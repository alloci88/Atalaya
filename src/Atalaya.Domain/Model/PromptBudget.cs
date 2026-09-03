using System.Globalization;

namespace Atalaya.Domain.Model;

/// <summary>
/// <b>Adónde va cada token de una sesión</b> (F18 §1). Es un derivado, como el coste: no se guarda
/// nada, se recalcula de los datos primarios —tokens, llamadas y el desglose por pasada— cada vez
/// que alguien pregunta.
/// <para>
/// <b>La pregunta que contesta.</b> «628.170 tokens de entrada» no dice nada accionable. Lo que sí
/// lo dice es en qué se reparten: cuánto de cada llamada es el código que se está auditando y
/// cuánto es andamiaje, y cuántas llamadas hace falta por unidad. En la línea base de F18 eso era
/// ~600 tokens de código dentro de ~28.500, con 11 llamadas por unidad — y esos dos números, y no
/// el total, son los que señalan dónde hay algo que hacer.
/// </para>
/// <para>
/// <b>Qué es «andamiaje» aquí.</b> Todo lo que viaja en una llamada y no es el código de la unidad:
/// las reglas, la rúbrica, el catálogo, la temática, los hallazgos conocidos, las herramientas, lo
/// que el propio proveedor añade por su cuenta y la conversación acumulada del turno. Se obtiene
/// por RESTA —entrada real menos código— y no sumando piezas, para que nada se quede fuera sin que
/// se note: lo que no sepamos nombrar sigue contando.
/// </para>
/// <para>
/// <b>La entrada real depende de quién cuenta.</b> Copilot mete la caché dentro de <c>In</c> y
/// Claude Code la deja fuera (D-785). Aquí se usa exactamente el mismo criterio que el cálculo de
/// credits, <see cref="CreditCalculator.AccountingOf"/>: dos reglas parecidas para el mismo número
/// acaban discrepando.
/// </para>
/// </summary>
public sealed record PromptBudget
{
    /// <summary>Llamadas al modelo de la sesión, tal y como las contó el proveedor.</summary>
    public int Calls { get; init; }

    /// <summary>Unidades con desglose de consumo. 0 en una sesión que no audita unidades.</summary>
    public int Units { get; init; }

    /// <summary>Prompts de unidad enviados: la suma de las pasadas de todas las unidades.</summary>
    public int Prompts { get; init; }

    /// <summary>Los tokens que compusieron los prompts, ya con la semántica de su proveedor.</summary>
    public long PromptTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    /// <summary>El desglose de Atalaya, sumado y pesado por las llamadas de cada pasada.</summary>
    public PromptComposition Composition { get; init; } = new();

    /// <summary>
    /// Hay desglose de composición con el que responder «de qué está hecha una llamada». False en
    /// las sesiones anteriores a F18 y en las que no auditan unidades: entonces esto sabe decir el
    /// reparto entrada/salida/caché y nada más, y lo dice en vez de estimar.
    /// </summary>
    public bool HasComposition { get; init; }

    /// <summary>Tokens de entrada por llamada, todo incluido.</summary>
    public double PerCall => Calls <= 0 ? 0 : (double)PromptTokens / Calls;

    /// <summary>
    /// Los tokens de CÓDIGO auditado que viajan en una llamada media. El código de una pasada va
    /// entero en cada una de sus llamadas —el prompt se reenvía completo—, así que el desglose se
    /// pesa por las llamadas de su pasada antes de promediar.
    /// </summary>
    public double CodePerCall => Calls <= 0 ? 0 : (double)Composition.Unidad / Calls;

    /// <summary>Lo que Atalaya pone en cada llamada y no es código: reglas, rúbrica, catálogo…</summary>
    public double OwnScaffoldPerCall => Calls <= 0 ? 0 : (double)Composition.Andamiaje / Calls;

    /// <summary>Todo lo que no es código, venga de donde venga. Por resta: nada se pierde.</summary>
    public double ScaffoldPerCall => Math.Max(0, PerCall - CodePerCall);

    /// <summary>
    /// Lo que el proveedor añade por su cuenta —sus herramientas, su propio sistema, la
    /// conversación del turno—: lo que queda tras descontar el código y lo que pone Atalaya.
    /// Puede salir negativo si la estimación se pasa; entonces se dice 0 y no un número inventado.
    /// </summary>
    public double ProviderScaffoldPerCall => Math.Max(0, ScaffoldPerCall - OwnScaffoldPerCall);

    /// <summary>Qué fracción de una llamada es el código auditado. 0 cuando no hay con qué decirlo.</summary>
    public double CodeShare => PerCall <= 0 ? 0 : CodePerCall / PerCall;

    public double CallsPerUnit => Units <= 0 ? 0 : (double)Calls / Units;

    /// <summary>
    /// <b>El suelo teórico de escritura de caché</b> (F18 §2), y con él el termómetro del prefijo
    /// inestable.
    /// <para>
    /// Con un prefijo estable de verdad, escribir en caché es <i>inevitable</i> exactamente una vez
    /// por contenido nuevo: una por el prefijo de la sesión y una por la parte variable de cada
    /// prompt de unidad. Todo lo demás son <b>re-escrituras</b>: el proveedor no encontró el
    /// prefijo y volvió a pagarlo a 1,25 × la entrada.
    /// </para>
    /// <para>
    /// <b>Es un suelo, no una predicción.</b> Cada turno añade la respuesta del modelo y el
    /// resultado de las tools, y eso también se escribe legítimamente; por eso lo que está por
    /// encima del suelo se llama re-escrituras <i>aproximadas</i> y no se presenta como una cuenta
    /// exacta. Lo que sí es concluyente es el orden de magnitud: si las re-escrituras se parecen al
    /// prefijo estable multiplicado por el número de prompts, el prefijo se está reescribiendo
    /// entero cada vez.
    /// </para>
    /// </summary>
    public long CacheWriteFloor { get; init; }

    /// <inheritdoc cref="CacheWriteFloor"/>
    public long CacheRewrites => Math.Max(0, CacheWriteTokens - CacheWriteFloor);

    /// <summary>El prefijo estable de esta sesión, en tokens: lo que la caché tendría que servir.</summary>
    public int StablePrefixTokens { get; init; }

    /// <summary>
    /// La línea que resume todo esto y que hasta F18 había que calcular a mano leyendo un informe.
    /// Vacía cuando no hay composición: sin ella no se puede separar código de andamiaje, y
    /// escribir la frase con un cero afirmaría que no viajó código.
    /// </summary>
    public string Line => !HasComposition || Calls <= 0
        ? string.Empty
        : $"andamiaje ≈ {Round(ScaffoldPerCall)} tokens/llamada · código auditado ≈ {Round(CodePerCall)} "
          + $"({Pct(CodeShare * 100)} %) · {Num(CallsPerUnit)} llamadas por unidad";

    private static string Round(double value)
        => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static string Pct(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Deriva el presupuesto de una sesión ya escrita. No lanza y no rellena: una sesión sin
    /// desglose devuelve lo que sí sabe, con <see cref="HasComposition"/> en false.
    /// </summary>
    public static PromptBudget From(AuditSession session)
    {
        TokenAccounting accounting = CreditCalculator.AccountingOf(session.Provider);
        long input = Math.Max(0, session.Usage.InputTokens);
        long read = Math.Max(0, session.Usage.CacheReadTokens);
        long write = Math.Max(0, session.Usage.CacheWriteTokens);

        // Con Copilot la entrada YA es el prompt entero; con Claude Code hay que sumarle la caché,
        // que va por fuera. Es la misma regla que usa el cálculo de credits (D-785).
        long promptTokens = accounting == TokenAccounting.InputIncludesCache
            ? input
            : input + read + write;

        var composition = new PromptComposition();
        bool has = false;
        int prompts = 0;
        long floor = 0;
        int stable = 0;

        foreach (UnitUsageBreakdown unit in session.UsageBreakdown)
        {
            foreach (PassUsage pass in unit.Passes)
            {
                prompts++;
                if (pass.Composition is not { } c)
                {
                    continue;
                }

                has = true;
                stable = Math.Max(stable, c.Estable);
                floor += c.Variable;

                // El prompt de la pasada viaja ENTERO en cada una de sus llamadas: el modelo no
                // recibe un resumen en la segunda vuelta, recibe la conversación desde el principio.
                composition += c.Times(Math.Max(1, pass.Calls));
            }
        }

        // El prefijo estable se escribe una vez por sesión — si la caché funciona.
        floor += stable;

        return new PromptBudget
        {
            Calls = Math.Max(0, session.Usage.Calls),
            Units = session.UsageBreakdown.Count,
            Prompts = prompts,
            PromptTokens = promptTokens,
            OutputTokens = Math.Max(0, session.Usage.OutputTokens),
            CacheReadTokens = read,
            CacheWriteTokens = write,
            Composition = composition,
            HasComposition = has,
            CacheWriteFloor = floor,
            StablePrefixTokens = stable,
        };
    }
}
