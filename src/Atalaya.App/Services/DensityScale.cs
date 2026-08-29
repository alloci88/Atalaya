namespace Atalaya.App.Services;

/// <summary>Qué mide el color de una celda del mapa (F10 §2).</summary>
public enum HeatMetric
{
    /// <summary>Deuda por cada mil líneas. Responde «dónde está más concentrado el problema».</summary>
    Densidad,

    /// <summary>Deuda total, sin normalizar. Responde «dónde hay más problemas».</summary>
    Deuda,
}

/// <summary>
/// Un paso de la escala secuencial: desde dónde vale, cómo se escribe en la leyenda y de qué
/// color va en cada tema.
/// </summary>
/// <param name="From">Umbral inferior, inclusivo. El primer paso empieza en 0.</param>
/// <param name="To">Umbral superior, exclusivo. <c>null</c> en el último paso.</param>
public sealed record HeatStep(int Index, double From, double? To, string Light, string Dark)
{
    public string For(bool dark) => dark ? Dark : Light;

    /// <summary>true si <paramref name="value"/> cae en este paso.</summary>
    public bool Contains(double value) => value >= From && (To is null || value < To);
}

/// <summary>
/// La escala de color del mapa de calor (F10 §2): <b>un solo tono</b>, del claro al oscuro, en
/// cinco pasos con umbrales fijos.
/// <para>
/// <b>Por qué un tono y no un arcoíris ni un semáforo.</b> La densidad de deuda es una magnitud
/// continua, y un arcoíris la convierte en categorías con fronteras inventadas (¿por qué el verde
/// acaba justo ahí?) además de no tener orden perceptual: nadie sabe si el cian va antes o después
/// del amarillo. Un solo tono del claro al oscuro se ordena solo, y funciona igual para quien no
/// distingue rojo de verde.
/// </para>
/// <para>
/// <b>Por qué violeta y no un tono cálido, que sería lo obvio para «calor».</b> Los cuatro colores
/// cálidos ya están tomados: rojo, naranja, amarillo y azul acero significan crítica, alta, media
/// y baja en toda la aplicación (<see cref="SeverityPalette"/>). Una rampa amarillo→naranja→rojo
/// sería letra por letra el vocabulario de severidad, y una celda granate se leería «aquí hay una
/// crítica» cuando lo que dice es «aquí la deuda está concentrada» — dos cosas distintas que
/// pueden no coincidir. El violeta es la familia del acento de la aplicación y no significa nada
/// más en ningún sitio, así que puede significar magnitud sin pisar a nadie.
/// </para>
/// <para>
/// <b>Por qué los umbrales son FIJOS y no cuantiles de los datos.</b> Con cuantiles, cada mapa
/// tendría su propia escala: el paso 5 de una aplicación limpia y el de una aplicación podrida
/// serían el mismo color diciendo cosas opuestas, y el mapa de hoy no se podría comparar con el de
/// la semana que viene. Con umbrales fijos, «paso 4» significa lo mismo en todas partes y siempre.
/// </para>
/// </summary>
public static class DensityScale
{
    /// <summary>
    /// Los cinco tonos, del más claro al más oscuro en tema claro; y al revés en tema oscuro, del
    /// más apagado al más brillante. <b>No es una inversión automática del mismo color</b>: son dos
    /// rampas elegidas para su fondo (misma razón que <see cref="SeriesColor"/>). Invertir la
    /// luminosidad de una rampa pensada para papel deja, sobre negro, cinco grises malvas que no se
    /// distinguen entre sí.
    /// </summary>
    private static readonly (string Light, string Dark)[] Tones =
    {
        ("#EFECFB", "#2F2A47"),
        ("#D2C8F1", "#453B78"),
        ("#AC99E4", "#5E4EAE"),
        ("#7F63D2", "#8069DC"),
        ("#4A2FA3", "#B3A2FF"),
    };

    /// <summary>
    /// Los umbrales de <b>densidad</b> (deuda por KLOC), y de dónde salen.
    /// <para>
    /// El ancla es el caso corriente: <b>una media (peso 2) en una unidad de 400 líneas</b> da 5 —
    /// el borde entre el paso 1 y el 2. Por debajo de ahí está el ruido de fondo de cualquier
    /// código vivo. A partir de ahí cada escalón multiplica por entre 2,5 y 3, igual que los pesos,
    /// de modo que subir un paso siempre significa lo mismo: «aquí hay del orden del triple».
    /// El paso 5 (≥ 100) es una unidad que debe una crítica cada 100 líneas: ahí no se parchea, se
    /// reescribe.
    /// </para>
    /// </summary>
    public static IReadOnlyList<HeatStep> Density { get; } = Build(new double[] { 5, 15, 40, 100 });

    /// <summary>
    /// Los umbrales de <b>deuda absoluta</b> (suma de pesos), para el modo alternativo. Anclados en
    /// la misma lectura: 2 es una media suelta, 5 es una alta, 15 son tres altas y 40 son cuatro
    /// críticas. Son otra pregunta, así que son otros umbrales: reutilizar los de densidad
    /// convertiría «40 puntos de deuda» en «40 por KLOC» sin avisar.
    /// </summary>
    public static IReadOnlyList<HeatStep> Debt { get; } = Build(new double[] { 2, 5, 15, 40 });

    /// <summary>Cuántos pasos tiene la escala. La leyenda los escribe todos, siempre.</summary>
    public static int StepCount => Tones.Length;

    /// <summary>
    /// El gris de «no auditada». <b>Nunca es el color frío de la escala</b>: si lo fuera, una
    /// unidad que nadie ha mirado se leería como una unidad limpia, que es la mentira que esta
    /// vista existe para no contar. Va además con trama diagonal, porque la textura se distingue
    /// del color incluso en una impresión en blanco y negro.
    /// </summary>
    public static SeriesColor Unknown { get; } = new("no auditada", "#DCDEE2", "#3A3F47");

    /// <summary>El trazo de la trama diagonal del gris de «no auditada».</summary>
    public static SeriesColor UnknownHatch { get; } = new("trama", "#B9BDC4", "#4E545E");

    /// <summary>Los pasos de la métrica pedida.</summary>
    public static IReadOnlyList<HeatStep> For(HeatMetric metric)
        => metric == HeatMetric.Deuda ? Debt : Density;

    /// <summary>
    /// En qué paso cae un valor. <c>null</c> es «desconocido» y no tiene paso: quien pregunte por
    /// el color de un desconocido se lleva <c>null</c> y tiene que decidir qué pinta, que es
    /// justo la decisión que no se puede tomar por descuido.
    /// </summary>
    public static HeatStep? StepOf(double? value, HeatMetric metric)
        => value is not { } v ? null : For(metric).LastOrDefault(s => s.Contains(v)) ?? For(metric)[^1];

    /// <summary>Cómo se escribe el tramo de un paso en la leyenda: «5 – 15», «≥ 100».</summary>
    public static string RangeLabel(HeatStep step)
        => step.To is null
            ? $"≥ {Num(step.From)}"
            : step.Index == 0
                ? $"< {Num(step.To.Value)}"
                : $"{Num(step.From)} – {Num(step.To.Value)}";

    private static string Num(double value) => value.ToString("0.##", AppCulture.Display);

    private static IReadOnlyList<HeatStep> Build(double[] cuts)
    {
        var steps = new List<HeatStep>(Tones.Length);
        for (int i = 0; i < Tones.Length; i++)
        {
            double from = i == 0 ? 0 : cuts[i - 1];
            double? to = i < cuts.Length ? cuts[i] : null;
            steps.Add(new HeatStep(i, from, to, Tones[i].Light, Tones[i].Dark));
        }

        return steps;
    }
}
