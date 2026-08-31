using System.Globalization;

namespace Atalaya.App.Services;

/// <summary>
/// El ÚNICO sitio donde una proporción se convierte en texto (BUGFIX-REDONDEO).
/// <para>
/// El parte: con 3 unidades auditadas de 1.335, la cobertura salía como <b>0 %</b> en la tarjeta de
/// Métricas, en los roscos y en el Portafolio. El dato real es 0,2 %. Decir 0 % cuando hay trabajo
/// hecho es un número que miente por redondeo — la misma falta que la aplicación persigue en todo
/// lo demás— y encima desanima justo cuando se empieza.
/// </para>
/// <para>
/// <b>La regla: el redondeo nunca crea un extremo falso.</b> Los extremos significan algo —«nada
/// mirado» y «no queda nada»—, así que solo pueden escribirse cuando son verdad. Un 0 % falso
/// borra el trabajo hecho; un 100 % falso es peor, porque <b>cierra la pregunta</b>: nadie vuelve a
/// mirar una app que dice estar al 100 %.
/// </para>
/// <para>
/// <b>Y la precisión es la mínima que no miente.</b> Decimales por todas partes es ruido: «42 %» se
/// lee de un vistazo y «42,0 %» no dice nada más. Solo aparecen cuando el entero se comería el
/// dato.
/// </para>
/// </summary>
public static class PercentText
{
    /// <summary>
    /// Por debajo de esto, un porcentaje entero perdería la escala: 4 % y 4,3 % no son lo mismo
    /// cuando el número es pequeño, y por encima de 10 el decimal ya no aporta.
    /// </summary>
    private const double DecimalsBelow = 10.0;

    /// <summary>
    /// El suelo de lo que se puede escribir con un decimal. Por debajo no se inventa un 0,0: se
    /// dice «menos de esto», que es lo único cierto que cabe en el hueco.
    /// </summary>
    private const double SmallestShown = 0.1;

    /// <summary>
    /// Una proporción 0..1 como texto (0,002 → «0,2 %»).
    /// <para>
    /// Fuera de rango se recorta: un cálculo que devuelva 1,0000001 por coma flotante no puede
    /// convertirse en «100,00001 %», y tampoco en un extremo falso — si el llamante quiere decir
    /// «completo» tiene que darle un 1 exacto.
    /// </para>
    /// </summary>
    /// <param name="fraction">La proporción, entre 0 y 1.</param>
    public static string Of(double fraction)
    {
        if (double.IsNaN(fraction))
        {
            return "—";
        }

        return Format(Math.Clamp(fraction, 0, 1) * 100, exactZero: fraction <= 0, exactFull: fraction >= 1);
    }

    /// <summary>
    /// La forma preferida: <paramref name="part"/> de <paramref name="whole"/>.
    /// <para>
    /// Se prefiere a <see cref="Of(double)"/> porque los extremos se deciden con los ENTEROS y no
    /// con la división: <c>1334/1335</c> en coma flotante es 0,99925…, y preguntarle a un
    /// <c>double</c> si eso «es 1» es exactamente cómo nacen los 100 % falsos. Aquí la pregunta es
    /// otra: ¿queda alguna sin auditar? Si queda una, no es 100 %, y punto.
    /// </para>
    /// </summary>
    public static string Of(int part, int whole)
    {
        if (whole <= 0)
        {
            return "—";   // sin denominador no hay proporción: no se inventa un 0 %
        }

        int clamped = Math.Clamp(part, 0, whole);
        return Format(100.0 * clamped / whole, exactZero: clamped == 0, exactFull: clamped == whole);
    }

    /// <summary>
    /// El texto de un porcentaje ya en 0..100, con quién decide los extremos por separado.
    /// </summary>
    /// <param name="exactZero">No hay NADA. Es el único caso que puede escribir «0 %».</param>
    /// <param name="exactFull">No queda nada. Es el único caso que puede escribir «100 %».</param>
    private static string Format(double pct, bool exactZero, bool exactFull)
    {
        if (exactZero)
        {
            return Write(0, decimals: 0);
        }

        if (exactFull)
        {
            return Write(100, decimals: 0);
        }

        // Hay algo, pero tan poco que un decimal lo redondearía a cero. «0,0 %» sería la misma
        // mentira con una coma dentro, así que se dice el límite.
        if (pct < SmallestShown)
        {
            return "< " + Write(SmallestShown, decimals: 1);
        }

        // Y su espejo, que es el caso grave: falta algo, pero tan poco que se escribiría 100 %.
        if (pct > 100 - SmallestShown)
        {
            return "> " + Write(100 - SmallestShown, decimals: 1);
        }

        // Con un decimal, el redondeo todavía puede llevarse el número a un extremo: 99,97 % se
        // redondea a 100,0. Se baja al valor representable más cercano que sigue diciendo la verdad.
        double rounded = Math.Round(pct, 1, MidpointRounding.AwayFromZero);
        if (rounded <= 0)
        {
            return "< " + Write(SmallestShown, decimals: 1);
        }

        if (rounded >= 100)
        {
            return "> " + Write(100 - SmallestShown, decimals: 1);
        }

        // Con entero, lo mismo: 99,6 % no puede convertirse en «100 %», ni 0,4 % en «0 %».
        if (rounded >= DecimalsBelow)
        {
            double asInteger = Math.Round(rounded, MidpointRounding.AwayFromZero);
            return asInteger >= 100 ? Write(rounded, decimals: 1) : Write(asInteger, decimals: 0);
        }

        return Write(rounded, decimals: 1);
    }

    /// <summary>
    /// El número con su signo, en la cultura de la aplicación y con el espacio fino que manda la
    /// tipografía española. Nunca la cultura de la máquina: F8.1 (D-522) lo dejó dicho.
    /// </summary>
    private static string Write(double value, int decimals)
        => value.ToString(decimals == 0 ? "0" : "0.0", AppCulture.Display) + " %";
}
