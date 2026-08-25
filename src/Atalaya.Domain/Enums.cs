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
/// Modo de una sesión de auditoría. Incluye los modos de auditoría propiamente
/// dichos (§0) y los eventos de sistema que también generan una sesión (§5).
/// </summary>
public enum AuditMode
{
    Integral,
    Lotes,
    Superficial,
    Verify,
    /// <summary>Cierre de ciclo (§5.1): asciende confianzas y abre el ciclo siguiente.</summary>
    Cierre,
    /// <summary>Reset de auditoría (§5.5): abre ciclo nuevo sin borrar nada.</summary>
    Reset,
    /// <summary>Arreglo asistido supervisado (§5.7, H9): registra coste de tokens.</summary>
    Fix,
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
