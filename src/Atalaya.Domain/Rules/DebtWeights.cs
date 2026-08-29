namespace Atalaya.Domain.Rules;

/// <summary>
/// Cuánto pesa un hallazgo según su severidad (F10 §1). <b>Estas cuatro constantes son la única
/// definición de «deuda» que hay en Atalaya</b>: quien quiera cambiar la escala la cambia aquí y
/// se entera todo lo que la usa.
/// <para>
/// <b>Por qué 10 · 5 · 2 · 1 y no 4 · 3 · 2 · 1.</b> Una escala lineal diría que diez hallazgos
/// de severidad baja son un problema mayor que dos críticos, y eso es falso en el único sentido
/// que importa aquí: el orden en que hay que atacar el código. La progresión es
/// aproximadamente geométrica —cada escalón vale entre dos y tres veces el anterior— para que
/// <b>una crítica no se pueda diluir</b> en un mar de bajas. Con estos pesos hacen falta diez
/// bajas, cinco medias o dos altas para igualar una crítica, que es la lectura que ya se usa al
/// priorizar: nadie deja una crítica abierta para cerrar cinco medias.
/// </para>
/// <para>
/// <b>Y por qué son constantes y no un ajuste.</b> Un peso configurable convierte el mapa de dos
/// máquinas en dos mapas distintos del mismo código, y la comparación entre aplicaciones —que es
/// para lo que sirve la vista— dejaría de significar nada. Si algún día se cambian, se cambian
/// para todo el portafolio y a la vez.
/// </para>
/// </summary>
public static class DebtWeights
{
    public const int Critica = 10;
    public const int Alta = 5;
    public const int Media = 2;
    public const int Baja = 1;

    /// <summary>El peso de una severidad. Exhaustivo por construcción: la enumeración no crece.</summary>
    public static int Of(Severity severity) => severity switch
    {
        Severity.Critica => Critica,
        Severity.Alta => Alta,
        Severity.Media => Media,
        _ => Baja,
    };

    /// <summary>
    /// La deuda de un conjunto de severidades. Es la suma de sus pesos y nada más: no hay tope,
    /// no hay saturación y no hay media. Una unidad con veinte hallazgos debe veinte veces.
    /// </summary>
    public static int Sum(IEnumerable<Severity> severities) => severities.Sum(Of);

    /// <summary>
    /// La densidad: deuda por cada mil líneas. <c>null</c> cuando no hay LOC que dividir —una
    /// unidad de 0 líneas no tiene densidad, y devolver 0 diría que está limpia.
    /// <para>
    /// Se normaliza por tamaño porque sin normalizar el mapa sería un mapa del tamaño del código:
    /// la clase de 5 000 líneas saldría siempre la peor por el mero hecho de ser grande, y eso ya
    /// se ve en el área de su celda.
    /// </para>
    /// </summary>
    public static double? Density(int debt, int loc)
        => loc <= 0 ? null : debt * 1000.0 / loc;
}
