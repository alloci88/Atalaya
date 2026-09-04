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
    /// La palabra del <b>modo exhaustivo</b> (R2 §1), escrita UNA vez. La dicen la cabecera del
    /// informe, el pie en vivo y el manual, y tres copias de una palabra son tres sitios donde
    /// cambiarla y dos donde olvidarse.
    /// </summary>
    public const string Exhaustive = "exhaustivo";

    /// <summary>
    /// Con qué se auditó, para la cabecera del informe y para el pie: «Lotes», o «Lotes ·
    /// exhaustivo» cuando cada pasada fue una petición nueva en vez de un turno de la conversación
    /// de la unidad. Sin esto, dos sesiones que cuestan el triple la una que la otra se leen igual.
    /// </summary>
    public static string Describe(AuditMode mode, bool exhaustive)
        => exhaustive ? $"{mode} · {Exhaustive}" : mode.ToString();

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

/// <summary>
/// Qué provocó una sesión de auditoría (F9 §6). No cambia nada de cómo se audita: distingue la
/// cobertura inicial del MANTENIMIENTO, que es la operación de cada sprint, para que Métricas pueda
/// separarlas algún día sin tener que reinterpretar sesiones antiguas.
/// </summary>
public enum SessionTrigger
{
    /// <summary>Alguien eligió las unidades a mano. Es el valor de todas las sesiones anteriores a F9.</summary>
    Manual,

    /// <summary>Salió de «Seleccionar cambiadas»: se audita lo que ha cambiado desde su auditoría.</summary>
    Deriva,
}

/// <summary>
/// La LUPA de un ciclo (F17): qué busca el auditor mientras dura. <see cref="General"/> es el
/// criterio completo de siempre y el único que mantiene todo el catálogo; las demás acotan el
/// encargo a una familia de defectos y NADA fuera de ella se reporta. Un hallazgo lleva la
/// temática del ciclo que lo detectó; lo anterior a F17 se lee como General, que es lo que era.
/// <para>
/// Catálogo cerrado de la casa, versionado junto a la rúbrica de severidad (mismo sitio, mismo
/// régimen): los criterios de cada una viven en <c>ThemeCatalog</c>, en <c>Atalaya.Copilot</c>.
/// Aquí solo está el vocabulario, que es lo que el dominio necesita para guardar y comparar.
/// </para>
/// </summary>
public enum AuditTheme
{
    /// <summary>El criterio completo. Es el ciclo de referencia y el valor de todo lo anterior a F17.</summary>
    General,

    /// <summary>Secretos, validación de entradas, inyección, transporte, permisos, criptografía casera.</summary>
    Seguridad,

    /// <summary>Algoritmia cara, colecciones ineficientes, E/S y llamadas redundantes, recursos no reutilizados.</summary>
    Rendimiento,

    /// <summary>Nulos, índices, excepciones tragadas, casos límite, contratos incumplidos, using/dispose.</summary>
    Fiabilidad,

    /// <summary>Carreras, bloqueos, .Result/.Wait, estado compartido mutable, deadlocks.</summary>
    Concurrencia,

    /// <summary>Duplicación, nomenclatura, documentación que miente, complejidad y tamaño, código muerto.</summary>
    Mantenibilidad,
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
