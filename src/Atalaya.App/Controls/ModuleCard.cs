using System.Windows.Media;

namespace Atalaya.App.Controls;

/// <summary>Un tramo de la tira de severidades de una tarjeta, o del termómetro de la cabecera.</summary>
/// <param name="Share">Su parte del total, 0..1. Es lo que decide el ancho.</param>
public sealed record HeatSegment(string Label, int Count, double Share, Brush Brush, string Tooltip)
{
    /// <summary>Un tramo sin nada que repartir no se dibuja.</summary>
    public bool HasWidth => Share > 0;
}

/// <summary>
/// La tarjeta de un módulo: el nivel 1 del mapa (F10.2 §1).
/// <para>
/// <b>Por qué una tarjeta y no una celda de treemap.</b> Con 925 unidades de las que 923 están sin
/// auditar, el treemap de hojas dibujaba 923 rectángulos grises idénticos: ninguna etiqueta cabía,
/// la textura tapaba las dos celdas con dato y la pregunta que contestaba —«dónde está la deuda»—
/// no se podía contestar con esos datos. Veintidós tarjetas contestan la que sí: <b>¿dónde miro
/// ahora?</b>, que es densidad conocida <i>y</i> volumen sin mirar, cada una en su canal.
/// </para>
/// </summary>
/// <param name="Name">Cómo se rotula: sin el nombre de la aplicación (F10.1c).</param>
/// <param name="FullName">El nombre entero, para el tooltip y para la lámina.</param>
/// <param name="Density">
/// El color de la densidad de <b>lo auditado</b>, o <c>null</c> cuando no hay nada auditado.
/// </param>
/// <param name="Stripe">
/// Lo que se pinta de verdad en el borde: <see cref="Density"/>, o el gris de «desconocida». Va
/// resuelto y no como un null que la vista tenga que interpretar — interpretarlo en XAML es cómo
/// se acaba pintando el paso más frío donde no se ha medido nada.
/// </param>
/// <param name="CoverageBar">
/// Los dos tramos de la barra de cobertura: lo auditado y lo que nadie ha mirado. <b>Aquí es donde
/// vive la honestidad del «no auditado»</b> — como dato medido y con su porcentaje escrito al
/// lado, en vez de como una textura que invade la vista entera.
/// </param>
/// <param name="Attention">La puntuación del orden por defecto, con su desglose en el tooltip.</param>
public sealed record ModuleCard(
    string Name,
    string FullName,
    string Size,
    double Coverage,
    IReadOnlyList<HeatSegment> CoverageBar,
    string CoverageText,
    Brush? Density,
    Brush Stripe,
    string DensityText,
    string DensityCaption,
    IReadOnlyList<HeatSegment> Severities,
    string SeverityText,
    double Attention,
    string AttentionText,
    string Tooltip,
    object? Payload)
{
    /// <summary>Lo auditado, 0..1, para la barra. Su complemento es lo que nadie ha mirado.</summary>
    public double Audited => Math.Clamp(Coverage, 0, 1);

    /// <summary>Y lo que falta, que es el dato del que trata media vista.</summary>
    public double Pending => 1 - Audited;

    /// <summary>Hay algo que repartir en la tira de severidades.</summary>
    public bool HasFindings => Severities.Any(s => s.Count > 0);
}
