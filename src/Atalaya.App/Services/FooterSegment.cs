namespace Atalaya.App.Services;

/// <summary>
/// Un trozo del pie de una sesión (F17-RETOQUE): lo que dice, en sus formas de más larga a más
/// corta, y con qué prioridad cede sitio cuando no cabe todo.
/// <para>
/// <b>La regla de la casa:</b> o cabe, o se abrevia con acceso al detalle, nunca texto truncado a
/// media palabra. Por eso un segmento no lleva UN texto sino sus <see cref="Candidates"/>, de la
/// forma completa a la mínima («28.050 entrada · 15.670 salida · caché …» → «79.000 tokens» →
/// «tokens: ver informe»), y quien lo pinta elige la más larga que cabe. El detalle completo
/// viaja siempre en el tooltip.
/// </para>
/// </summary>
/// <param name="Candidates">De la forma completa a la mínima. Nunca vacía.</param>
/// <param name="Priority">
/// Quién cede primero cuando falta sitio: 0 no cede nunca (llamadas, progreso); a mayor número,
/// antes se abrevia y, agotadas sus formas, antes desaparece. El orden de la casa para el consumo
/// es llamadas → coste → tokens: llamadas 0, coste 1, tokens 2.
/// </param>
/// <param name="Bold">Peso de la métrica que manda.</param>
/// <param name="Opacity">Lo secundario va atenuado, como venía yendo.</param>
/// <param name="TooltipOnly">
/// El trozo <b>no se pinta nunca en la línea</b>: vive solo en el tooltip (F23 §6). Es para el
/// diagnóstico del coste —el desglose de caché, el reparto, la composición—, que es exactamente lo
/// que el informe manda al anexo: sigue estando y sigue a un gesto de distancia, pero no delante de
/// quien está mirando cómo va su auditoría.
/// </param>
public sealed record FooterSegment(
    IReadOnlyList<string> Candidates,
    int Priority = 0,
    bool Bold = false,
    double Opacity = 1.0,
    bool TooltipOnly = false)
{
    public static FooterSegment Of(string text, int priority = 0, bool bold = false, double opacity = 1.0)
        => new(new[] { text }, priority, bold, opacity);

    /// <summary>Lo mismo, pero solo para el tooltip: no ocupa sitio en la línea.</summary>
    public static FooterSegment Hidden(string text)
        => new(new[] { text }, Priority: int.MaxValue, TooltipOnly: true);

    /// <summary>La forma completa: la primera. Es lo que va al tooltip.</summary>
    public string Full => Candidates[0];
}
