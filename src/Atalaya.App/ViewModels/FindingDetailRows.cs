using Atalaya.Domain;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Un campo de la tarjeta de metadatos: etiqueta, valor y por qué está ahí.</summary>
/// <param name="Mono">
/// Se pinta en monoespaciada: identificadores como el ruleId o el sha, donde la forma del texto
/// ES el dato y una proporcional los vuelve ilegibles.
/// </param>
public sealed record MetaRow(string Label, string Value, string? Tooltip = null, bool Mono = false);

/// <summary>
/// Una fila de texto que puede ser larga: se pliega a dos líneas y se despliega con «ver más».
/// <para>
/// Antes se truncaba con puntos suspensivos y la justificación de una disputa —que es justo el
/// texto que hay que leer para decidir— se quedaba en «el patrón de este proyecto es…». Truncar
/// esconde el dato; plegar lo deja a un clic.
/// </para>
/// </summary>
public abstract partial class ExpandableRow : ObservableObject
{
    /// <summary>A partir de aquí el texto no cabe en dos líneas y merece plegarse.</summary>
    public const int LongTextThreshold = 150;

    /// <summary>Lo que ocupan dos líneas de detalle. Es la altura de lo plegado.</summary>
    public const double CollapsedHeight = 34;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailMaxHeight))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _isExpanded;

    public required string Detail { get; init; }

    public bool IsLong => Detail.Length > LongTextThreshold;

    /// <summary>
    /// Plegado: dos líneas. Desplegado —o corto de por sí—: las que hagan falta, sin tope. Un
    /// texto que ya cabía no se pliega, que si no el «ver más» sale en eventos de una frase.
    /// <para>
    /// Va en altura y no en número de líneas porque <c>TextBlock</c> de WPF no tiene
    /// <c>MaxLines</c>: eso es de WinUI. Dos líneas de 11,5 px son
    /// <see cref="CollapsedHeight"/> px.
    /// </para>
    /// </summary>
    public double DetailMaxHeight => IsExpanded || !IsLong ? double.PositiveInfinity : CollapsedHeight;

    public string ToggleLabel => IsExpanded ? "ver menos" : "ver más";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Un evento del historial, ya traducido y con su icono (F5.5 §5).</summary>
public sealed partial class HistoryRow : ExpandableRow
{
    public required FindingEvent Event { get; init; }

    public required DateTimeOffset Utc { get; init; }

    public required string By { get; init; }

    /// <summary>El nombre del evento en castellano. Ver <see cref="FindingEventNames"/>.</summary>
    public string Label => FindingEventNames.Display(Event);

    public string Icon => FindingEventNames.Icon(Event);

    public string When => Utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    /// <summary>Quién y cuándo, en una línea: es la cabecera del evento.</summary>
    public string Header => string.IsNullOrWhiteSpace(By) ? When : $"{When} · {By}";
}

/// <summary>Un comentario del hallazgo. El prompt de arreglo también vive aquí, y es largo.</summary>
public sealed partial class CommentRow : ExpandableRow
{
    public required string By { get; init; }

    public required DateTimeOffset Utc { get; init; }

    public string? Kind { get; init; }

    public string KindLabel => Kind switch
    {
        null or "" => "Comentario",
        "fix-prompt" => "Prompt de arreglo",
        _ => Kind,
    };

    public string Header => $"{Utc.ToLocalTime():dd/MM/yyyy HH:mm} · {By} · {KindLabel}";
}

/// <summary>
/// Los nombres de los eventos del historial <b>en castellano</b> (F5.5 §5).
/// <para>
/// <see cref="FindingEvent"/> es una enumeración de C#: sus miembros están en inglés y sin tildes
/// porque el lenguaje es así. Volcarla con <c>ToString()</c> en la ficha escribía «SeverityChanged»
/// en una interfaz en castellano — el mismo escape del modelo a la interfaz que ya se corrigió con
/// la severidad en F5.4. Traducir es trabajo de la vista, no del modelo.
/// </para>
/// </summary>
public static class FindingEventNames
{
    public static string Display(FindingEvent e) => e switch
    {
        FindingEvent.Detected => "Detectado",
        FindingEvent.Confirmed => "Confirmado",
        FindingEvent.Resolved => "Resuelto",
        FindingEvent.Reopened => "Reabierto",
        FindingEvent.Assigned => "Asignado",
        FindingEvent.SeverityChanged => "Severidad cambiada",
        FindingEvent.Silenced => "Silenciado",
        FindingEvent.Unsilenced => "Des-silenciado",
        FindingEvent.Commented => "Comentado",
        FindingEvent.Recurrence => "Reaparición",
        FindingEvent.FixProposed => "Arreglo propuesto",
        FindingEvent.Disputed => "Disputado",
        FindingEvent.DisputeCleared => "Disputa cerrada",
        FindingEvent.NotLocated => "No localizado",
        FindingEvent.Reanchored => "Re-anclado",
        _ => e.ToString(),
    };

    /// <summary>
    /// El glifo de la línea de tiempo. Todos son texto plano —nada de emoji— porque un emoji se
    /// pinta con su propio color y se salta el del tema; la balanza de la disputa ya lo aprendió
    /// en F5.4 y por eso lleva el selector de presentación de texto.
    /// </summary>
    public static string Icon(FindingEvent e) => e switch
    {
        FindingEvent.Detected => "✱",
        FindingEvent.Confirmed => "✓",
        FindingEvent.Resolved => "●",
        FindingEvent.Reopened => "↺",
        FindingEvent.Assigned => "→",
        FindingEvent.SeverityChanged => "▲",
        FindingEvent.Silenced => "∅",
        FindingEvent.Unsilenced => "◎",
        FindingEvent.Commented => "❞",
        FindingEvent.Recurrence => "↻",
        FindingEvent.FixProposed => "⚒",
        FindingEvent.Disputed => "⚖︎",
        FindingEvent.DisputeCleared => "⚖︎",
        FindingEvent.NotLocated => "◌",
        FindingEvent.Reanchored => "⌖",
        _ => "•",
    };
}

/// <summary>Los motivos de silencio, escritos como se leen y no como se declaran en C#.</summary>
public static class SilenceReasonNames
{
    public static string Display(SilenceReason reason) => reason switch
    {
        SilenceReason.FalsoPositivo => "Falso positivo",
        SilenceReason.DeudaAceptada => "Deuda aceptada",
        SilenceReason.DecisionArquitectonica => "Decisión arquitectónica",
        _ => "Otro",
    };
}

/// <summary>El modo de la sesión que produjo el hallazgo, en castellano.</summary>
public static class AuditModeNames
{
    public static string Display(AuditMode mode) => mode switch
    {
        AuditMode.Integral => "Auditoría integral",
        AuditMode.Lotes => "Auditoría por lotes",
        AuditMode.Superficial => "Auditoría superficial",
        AuditMode.Verify => "Verificación",
        AuditMode.Cierre => "Cierre de ciclo",
        AuditMode.Reset => "Reset de auditoría",
        AuditMode.Fix => "Arreglo asistido",
        _ => mode.ToString(),
    };
}

/// <summary>El estado del hallazgo, en castellano y con su tooltip.</summary>
public static class FindingStatusNames
{
    public static string Display(FindingStatus status) => status switch
    {
        FindingStatus.Activo => "Activo",
        FindingStatus.Resuelto => "Resuelto",
        _ => "Silenciado",
    };

    public static string Help(FindingStatus status) => status switch
    {
        FindingStatus.Activo => "Sigue abierto: cuenta en informes y auditorías.",
        FindingStatus.Resuelto => "Cerrado. Reabrir lo devuelve a activo con traza.",
        _ => "Oculto de informes y auditorías por decisión humana.",
    };
}

/// <summary>Una opción de un combo: el valor del modelo con el texto que lee una persona.</summary>
public sealed record Labeled<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>La confianza, escrita entera para que el chip se explique solo.</summary>
public static class ConfidenceNames
{
    public static string Display(Confidence confidence) => confidence switch
    {
        Confidence.Alta => "Confianza alta",
        Confidence.Media => "Confianza media",
        _ => "Confianza baja",
    };

    public static string Help(Confidence confidence) => confidence switch
    {
        Confidence.Alta => "Confirmado varias veces o por una auditoría integral.",
        Confidence.Media => "Confirmado, pero sin el respaldo suficiente para subir a alta.",
        _ => "Detectado una sola vez y en modo superficial: aún sin respaldo.",
    };
}

/// <summary>
/// Sobre qué recae un silencio (F5.12). Son dos preguntas distintas con la misma disciplina:
/// «este caso concreto es un falso positivo aquí» y «este TIPO de problema no me interesa en este
/// proyecto».
/// </summary>
public enum SilenceScope
{
    /// <summary>El comportamiento de siempre: un silencio por ULID de hallazgo.</summary>
    Hallazgo,

    /// <summary>Silencio por patrón, por-aplicación. Nunca global al hub.</summary>
    Patron,
}

/// <summary>
/// Una opción del selector de alcance con su consecuencia escrita. La consecuencia va en el
/// modelo y no en el XAML porque es la parte que hay que poder comprobar: es lo único que separa
/// las dos opciones a ojos de quien las lee por primera vez.
/// <para>
/// El texto es mutable porque nombra la regla y la aplicación del hallazgo abierto, y la ficha se
/// recarga sobre el mismo view-model. La lista de opciones, en cambio, se construye UNA vez: si se
/// reconstruyera en cada recarga, el botón de radio marcado se perdería a media edición.
/// </para>
/// </summary>
public sealed partial class SilenceScopeOption : ObservableObject
{
    public SilenceScopeOption(SilenceScope value, Action<SilenceScope> onSelected)
    {
        Value = value;
        _onSelected = onSelected;
    }

    private readonly Action<SilenceScope> _onSelected;

    public SilenceScope Value { get; }

    [ObservableProperty] private string _label = string.Empty;

    [ObservableProperty] private string _consequence = string.Empty;

    /// <summary>El radio marcado. Elegir avisa al view-model; desmarcar no decide nada.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Si esta opción lleva debajo la caja del ejemplar (F5.12). Solo la del patrón: el alcance por
    /// patrón se define con una FRASE, y esa frase tiene que verse y poderse pulir antes de
    /// confirmar — es literalmente lo único que el auditor va a leer.
    /// </summary>
    public bool HasExemplar { get; init; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _onSelected(Value);
        }
    }
}
