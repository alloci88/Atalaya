namespace Atalaya.App.Services;

/// <summary>
/// La puntuación de <b>atención</b> de un módulo, con sus dos sumandos a la vista para poder
/// explicarla en un tooltip. Sin el desglose sería un número mágico, y un ranking que no se puede
/// explicar no se sigue.
/// </summary>
/// <param name="Risk">Lo que se ha MEDIDO que arde, atenuado por la confianza. 0..1.</param>
/// <param name="Ignorance">La parte del código de la aplicación que este módulo esconde. 0..1.</param>
/// <param name="Confidence">Cuánto pesa su densidad, según sobre cuánto código se midió. 0,5..1.</param>
public sealed record Attention(double Score, double Risk, double Ignorance, double Confidence);

/// <summary>
/// El orden «Atención» (F10.2 §1): <b>¿por dónde miro AHORA?</b>
/// <para>
/// Es una pregunta distinta de «¿dónde está la deuda?», y con cobertura baja —el estado normal de
/// una aplicación durante meses— es la única que se puede contestar. Ordenar por deuda con el 0,2 %
/// auditado ordena por las dos unidades que alguien miró; ordenar por tamaño ignora lo que ya se
/// sabe. Atención junta las dos mitades: <b>lo que arde</b> y <b>lo que no se ha mirado</b>.
/// </para>
/// <para>
/// <b>La ignorancia es riesgo.</b> Un módulo de 240 KLOC que nadie ha abierto no es un módulo
/// limpio: es un módulo del que no se sabe nada, y esconde más deuda posible que ningún otro. Por
/// eso pesa, y por eso un módulo grande sin auditar sube aunque su densidad conocida sea cero.
/// </para>
/// </summary>
public static class AttentionScore
{
    /// <summary>
    /// Cuánto pesa lo medido frente a lo ignorado. <b>Gana lo medido</b>: un problema comprobado
    /// es mejor motivo para ir a un sitio que la sospecha de que pueda haberlo. Pero no por mucho
    /// —60/40—, porque con cobertura baja «lo medido» son cuatro ficheros y lo ignorado es el
    /// resto de la aplicación.
    /// </summary>
    public const double RiskWeight = 0.6;

    /// <inheritdoc cref="RiskWeight"/>
    public const double IgnoranceWeight = 0.4;

    /// <summary>
    /// El suelo de la confianza. Una densidad medida sobre poco código <b>cuenta la mitad</b> que
    /// una medida sobre el módulo entero — pero cuenta: descontarla del todo enterraría el dato
    /// más accionable que tiene la herramienta, que es un fichero comprobadamente podrido.
    /// </summary>
    public const double ConfidenceFloor = 0.5;

    /// <summary>
    /// Con qué densidad se satura el riesgo: el umbral del <b>último paso de la rampa</b>. No es
    /// una constante nueva — es la misma que ya dice «a partir de aquí no se parchea, se
    /// reescribe» en la leyenda (<see cref="DensityScale"/>).
    /// </summary>
    public static double Ceiling => DensityScale.Density[^1].From;

    /// <summary>
    /// La atención de un módulo dentro de <b>su</b> aplicación.
    /// <para>
    /// <c>atención = 0,6 · riesgo + 0,4 · ignorancia</c>, donde
    /// <c>riesgo = min(1, densidad / 100) · confianza</c>,
    /// <c>confianza = 0,5 + 0,5 · (LOC auditadas / LOC del módulo)</c> y
    /// <c>ignorancia = LOC sin auditar del módulo / LOC de la aplicación</c>.
    /// </para>
    /// <para>
    /// <b>Los dos sumandos están anclados y no normalizados contra el máximo del día.</b> El
    /// riesgo se mide contra el techo de la rampa y la ignorancia contra el tamaño de la
    /// aplicación, así que la puntuación de un módulo no cambia porque se dé de alta otro — y el
    /// orden de ayer se puede comparar con el de hoy.
    /// </para>
    /// </summary>
    /// <param name="appLoc">Las líneas de TODA la aplicación: es contra lo que se mide ignorar.</param>
    public static Attention Of(HeatModule module, int appLoc)
    {
        double confidence = ConfidenceFloor + ((1 - ConfidenceFloor) * Math.Clamp(module.CoverageLoc, 0, 1));

        // Sin densidad medida no hay riesgo COMPROBADO que sumar. Lo que ese módulo pueda tener
        // entra por el otro sumando, que es exactamente lo que significa no haberlo mirado.
        double risk = module.Density is { } density
            ? Math.Clamp(density / Ceiling, 0, 1) * confidence
            : 0;

        double ignorance = appLoc <= 0
            ? 0
            : Math.Clamp((double)(module.Loc - module.AuditedLoc) / appLoc, 0, 1);

        return new Attention(
            (RiskWeight * risk) + (IgnoranceWeight * ignorance),
            risk,
            ignorance,
            confidence);
    }
}
