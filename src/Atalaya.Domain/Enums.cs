namespace Atalaya.Domain;

/// <summary>Los tres pilares de auditoría del sistema v4.</summary>
public enum Pillar
{
    Optimizacion,
    Mejoras,
    Errores,
}

/// <summary>Rúbrica de severidad (§0). La ordena de mayor a menor gravedad.</summary>
public enum Severity
{
    Critica,
    Alta,
    Media,
    Baja,
}

/// <summary>Confianza del hallazgo. Gobernada por <see cref="Confidence.ConfidenceMachine"/>.</summary>
public enum Confidence
{
    Alta,
    Media,
    Baja,
}

/// <summary>Ciclo de vida del hallazgo. Los hallazgos nunca se borran (§0, mejora 4).</summary>
public enum FindingStatus
{
    Activo,
    Resuelto,
    Silenciado,
}

/// <summary>Etiqueta del hallazgo: proviene del checklist o del criterio profesional.</summary>
public enum FindingTag
{
    Checklist,
    Criterio,
}

/// <summary>
/// Marca un modo de auditoría RETIRADO (F5.6 §2): ya no se puede lanzar desde la interfaz, pero
/// el valor sigue existiendo y leyéndose porque las sesiones históricas del hub lo referencian.
/// <para>
/// <b>Por qué un atributo propio y no <c>[Obsolete]</c>.</b> <c>Obsolete</c> avisa en cada USO,
/// y los usos que quedan son precisamente los que tienen que seguir vivos: el mapa JSON que carga
/// las sesiones antiguas, la máquina de confianza que interpreta su origen y el importador de V4.
/// En Domain y Storage los avisos son errores (§11), así que marcar con <c>Obsolete</c> obligaría
/// a silenciarlo en cada línea de lectura — ruido que acabaría escondiendo un aviso de verdad. Lo
/// que hace falta aquí es una declaración legible por código y por persona, no una advertencia.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class DeprecatedModeAttribute : Attribute
{
    public DeprecatedModeAttribute(string reason) => Reason = reason;

    /// <summary>Qué lo sustituye, en una línea.</summary>
    public string Reason { get; }
}

/// <summary>
/// Modo de una sesión de auditoría. Incluye los modos de auditoría propiamente
/// dichos (§0) y los eventos de sistema que también generan una sesión (§5).
/// <para>
/// Dos de ellos están RETIRADOS desde F5.6: el barrido por lotes con reconciliación los dejó sin
/// contenido propio. Ninguno se borra — ver <see cref="DeprecatedModeAttribute"/>.
/// </para>
/// </summary>
public enum AuditMode
{
    /// <summary>
    /// RETIRADO (F5.6 §2). Equivale a «seleccionar todo + lotes»: el barrido cubre lo mismo y
    /// además reconcilia. Se sigue leyendo: las sesiones antiguas del hub lo llevan.
    /// </summary>
    [DeprecatedMode("Equivale a seleccionar todas las unidades y auditarlas por lotes.")]
    Integral,

    Lotes,

    /// <summary>
    /// RETIRADO (F5.6 §2). Equivale a un tope de una sola pasada (<c>maxPassesPerUnit = 1</c>),
    /// que es un ajuste, no un modo. Se sigue leyendo: las sesiones antiguas del hub lo llevan.
    /// </summary>
    [DeprecatedMode("Equivale a un tope de una sola pasada por unidad (maxPassesPerUnit = 1).")]
    Superficial,

    Verify,
    /// <summary>Cierre de ciclo (§5.1): asciende confianzas y abre el ciclo siguiente.</summary>
    Cierre,
    /// <summary>Reset de auditoría (§5.5): abre ciclo nuevo sin borrar nada.</summary>
    Reset,
    /// <summary>Arreglo asistido supervisado (§5.7, H9): registra coste de tokens.</summary>
    Fix,
}

/// <summary>Lo que se sabe de un <see cref="AuditMode"/> sin tener que recordarlo.</summary>
public static class AuditModes
{
    /// <summary>
    /// True si el modo está retirado (F5.6 §2). Se lee del atributo, no de una lista paralela:
    /// una segunda lista es la que se queda sin actualizar (D-239).
    /// </summary>
    public static bool IsDeprecated(AuditMode mode) => Deprecation(mode) is not null;

    /// <summary>Por qué se retiró, o <c>null</c> si sigue vigente.</summary>
    public static string? Deprecation(AuditMode mode)
        => typeof(AuditMode)
            .GetField(mode.ToString())?
            .GetCustomAttributes(typeof(DeprecatedModeAttribute), false)
            .OfType<DeprecatedModeAttribute>()
            .FirstOrDefault()?
            .Reason;
}

/// <summary>Estado de una unidad dentro del inventario de un ciclo (§2).</summary>
public enum UnitState
{
    Pendiente,
    Auditada,
    Grande,
}

/// <summary>Motivo de un silencio (§2). Siempre lo decide una persona.</summary>
public enum SilenceReason
{
    FalsoPositivo,
    DeudaAceptada,
    DecisionArquitectonica,
    Otro,
}

/// <summary>Veredicto de una sesión <see cref="AuditMode.Verify"/> (§5.4).</summary>
public enum Verdict
{
    Confirmado,
    Resuelto,
    NoVerificable,
}

/// <summary>
/// Veredicto de reconciliación que el auditor emite sobre un hallazgo YA EXISTENTE de la unidad
/// que está auditando (F4, tool <c>report_verdicts</c>). Es el único mecanismo por el que un
/// hallazgo cambia de estado durante una sesión de auditoría: no hay resolución implícita.
/// </summary>
public enum ReconcileVerdict
{
    /// <summary>El problema sigue ahí → reconfirmación (máquina de confianza intacta).</summary>
    Presente,

    /// <summary>El problema ya no está → resuelto con la evidencia que aporta el auditor.</summary>
    Arreglado,

    /// <summary>No se puede determinar desde la unidad → <c>needsReview</c>.</summary>
    NoVerificable,

    /// <summary>
    /// El auditor sostiene que NUNCA fue un defecto (F5.1b) — una discrepancia de criterio con
    /// quien lo reportó, no un arreglo. No resuelve ni desactiva: marca el hallazgo como disputado
    /// y lo deja para una persona.
    /// <para>
    /// Sin esta casilla el modelo no tenía forma de expresar desacuerdo y usaba «arreglado», que
    /// cerraba el hallazgo en silencio. Mismo patrón que D-091: cuando el vocabulario no cubre lo
    /// que el auditor quiere decir, no calla — usa la casilla más cercana y la app registra algo
    /// falso.
    /// </para>
    /// </summary>
    NoEsDefecto,
}
