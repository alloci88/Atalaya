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
/// <param name="LightInk">
/// Con qué color se escribe ENCIMA de este paso en tema claro. Va por paso y no por una regla de
/// luminancia calculada al vuelo: la heurística daba blanco sobre #F1605D, que es un coral claro
/// donde lo legible es el negro. El contraste de cada pareja está verificado; una fórmula lo
/// vuelve a decidir en cada render y se equivoca justo en el borde.
/// </param>
public sealed record HeatStep(
    int Index, double From, double? To, string Light, string LightInk, string Dark, string DarkInk)
{
    public string For(bool dark) => dark ? Dark : Light;

    /// <summary>La tinta que se lee sobre este paso, en ese tema.</summary>
    public string InkFor(bool dark) => dark ? DarkInk : LightInk;

    /// <summary>true si <paramref name="value"/> cae en este paso.</summary>
    public bool Contains(double value) => value >= From && (To is null || value < To);
}

/// <summary>
/// La escala de color del mapa de calor (F10 §2, rampa de F10.1 §1): una <b>rampa secuencial
/// de la familia magma</b>, violeta → magenta → coral → ámbar, en cinco pasos con umbrales fijos.
/// <b>Es el único sitio donde vive la rampa</b>: la comparten el treemap, la leyenda, la tabla y
/// el inventario.
/// <para>
/// <b>Multi-tono, y sigue siendo secuencial.</b> Lo que ordena una escala secuencial no es tener
/// un solo tono: es que la <b>claridad sea monótona</b>. Magma la recorre entera de oscuro a
/// brillante mientras gira el matiz, así que se ordena sola, se distingue paso a paso de un
/// vistazo y sobrevive a una copia en blanco y negro y a cualquier daltonismo. Los cinco morados
/// de la primera entrega cumplían la regla y no la lectura: la diferencia entre el paso 2 y el 3
/// no se veía desde un metro, que es la distancia a la que se mira una diapositiva.
/// </para>
/// <para>
/// <b>Sigue sin ser un arcoíris ni un semáforo.</b> Un arcoíris no tiene orden perceptual —nadie
/// sabe si el cian va antes o después del amarillo— y un semáforo convierte una magnitud continua
/// en tres categorías con fronteras inventadas. Aquí la claridad crece sin volver atrás en ningún
/// paso, que es exactamente lo que un arcoíris no hace.
/// </para>
/// <para>
/// <b>Y los colores de severidad siguen reservados.</b> La rampa toca tonos cálidos, así que la
/// separación ya no es de paleta sino de <b>forma y sitio</b>: la severidad se escribe en píldoras
/// con texto (C/A/M/B) en los chips y en el detalle, y la rampa es solo <b>relleno</b> de celda
/// con su leyenda de cinco pasos al lado. Un degradado de cinco casillas y una píldora con una
/// letra dentro no se confunden ni puestos uno al lado del otro.
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
    /// <summary>La tinta oscura de la rampa. Sobre los pasos claros es lo único que se lee.</summary>
    private const string Black = "#101014";

    /// <inheritdoc cref="Black"/>
    private const string White = "#FFFFFF";

    /// <summary>
    /// Los cinco pasos, de menor a mayor densidad, con la tinta que se lee encima de cada uno.
    /// <para>
    /// <b>Dos rampas, no una invertida.</b> En tema claro la rampa va de ámbar pálido a violeta
    /// profundo (de claro a oscuro sobre papel); en oscuro, de violeta profundo a ámbar brillante.
    /// Cada una está elegida y verificada contra <b>su</b> superficie
    /// (<see cref="Surface"/>): invertir por código la del otro tema es lo que produce medias
    /// tintas que no contrastan con ningún fondo.
    /// </para>
    /// </summary>
    private static readonly (string Light, string LightInk, string Dark, string DarkInk)[] Tones =
    {
        ("#FEC98D", Black, "#5B2A78", White),
        ("#F1605D", Black, "#8C2981", White),
        ("#C43C75", White, "#C43C75", White),
        ("#8C2981", White, "#F1605D", Black),
        ("#4B1D6F", White, "#FEA772", Black),
    };

    /// <summary>
    /// La superficie sobre la que se dibuja el mapa, y contra la que se verificó el contraste de
    /// la rampa. La usan la vista y la lámina exportada: cambiarla en un sitio y no en el otro
    /// dejaría la exportación con una rampa comprobada contra un fondo que no es el suyo.
    /// </summary>
    public static SeriesColor Surface { get; } = new("superficie", "#F6F7FA", "#12151D");

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
            steps.Add(new HeatStep(
                i, from, to, Tones[i].Light, Tones[i].LightInk, Tones[i].Dark, Tones[i].DarkInk));
        }

        return steps;
    }
}
