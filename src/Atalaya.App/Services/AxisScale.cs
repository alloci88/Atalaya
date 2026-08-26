namespace Atalaya.App.Services;

/// <summary>
/// El eje Y de una gráfica: hasta dónde llega y dónde van sus marcas (F5.9 §2).
/// <para>
/// <b>Un solo eje por gráfica, siempre.</b> Esta clase calcula UNA escala, y las gráficas del
/// panel tienen una sola: dos magnitudes distintas se dibujan en dos gráficas, no en dos ejes
/// de la misma. Un eje secundario deja que quien dibuja elija la escala con la que se leen dos
/// series, y con eso se puede hacer que cualquier par de líneas se crucen donde uno quiera.
/// </para>
/// <para>
/// Las marcas son números REDONDOS (1, 2, 5 por década). Un eje que llega a 137 con marcas cada
/// 34,25 es técnicamente exacto e ilegible.
/// </para>
/// </summary>
public sealed record AxisScale(double Max, IReadOnlyList<double> Ticks)
{
    /// <summary>Cuántas marcas se buscan. Más de cuatro convierten la rejilla en ruido.</summary>
    public const int TargetTicks = 4;

    /// <summary>Un eje sin datos: de 0 a 1, para que la rejilla exista y no divida por cero.</summary>
    public static AxisScale Empty { get; } = new(1, new double[] { 0, 1 });

    /// <summary>Dónde cae un valor dentro del eje, de 0 (abajo) a 1 (arriba).</summary>
    public double Fraction(double value) => Max <= 0 ? 0 : Math.Clamp(value / Max, 0, 1);

    /// <summary>La escala que cubre <paramref name="dataMax"/> terminando en un número redondo.</summary>
    public static AxisScale For(double dataMax, int targetTicks = TargetTicks)
    {
        if (double.IsNaN(dataMax) || double.IsInfinity(dataMax) || dataMax <= 0)
        {
            return Empty;
        }

        double step = NiceStep(dataMax / Math.Max(1, targetTicks));
        double max = Math.Ceiling(dataMax / step) * step;
        var ticks = new List<double>();
        for (double t = 0; t <= max + step / 2; t += step)
        {
            ticks.Add(Math.Round(t, 6));
        }

        return new AxisScale(max, ticks);
    }

    /// <summary>El 1-2-5 de toda la vida, escalado a la decada del dato.</summary>
    private static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
