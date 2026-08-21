using Atalaya.Domain;

namespace Atalaya.Copilot;

/// <summary>A checklist rule: a stable id, its pillar, a short title and what to look for.</summary>
public sealed record RuleDef(string RuleId, Pillar Pillar, string Title, string Look);

/// <summary>
/// The versioned checklist catalog (§6.4). Each rule has a STABLE <c>ruleId</c> — the basis of
/// the fingerprint — so silences and dedupe survive across cycles and re-wordings. Findings may
/// also carry a <c>criterio.&lt;área&gt;</c> id for professional-judgement items.
/// </summary>
public static class RuleCatalog
{
    public static IReadOnlyList<RuleDef> Rules { get; } = new RuleDef[]
    {
        // errores
        new("errores.async.mal-usado", Pillar.Errores, "async/await mal usado",
            "async void, .Result/.Wait() bloqueantes, falta de ConfigureAwait en librería, fire-and-forget sin control"),
        new("errores.recursos.idisposable-no-liberado", Pillar.Errores, "IDisposable sin liberar",
            "objetos IDisposable creados sin using/dispose; streams, conexiones, handles"),
        new("errores.recursos.no-liberado", Pillar.Errores, "Recurso no liberado en ruta de error",
            "recurso adquirido que no se libera si una excepción interrumpe el flujo"),
        new("errores.enumeracion.multiple", Pillar.Errores, "Enumeración múltiple de IEnumerable",
            "IEnumerable recorrido más de una vez causando re-ejecución o consultas repetidas"),
        new("errores.seguridad.secreto-en-codigo", Pillar.Errores, "Secreto en código",
            "claves, tokens, contraseñas o cadenas de conexión embebidas en el fuente"),
        new("errores.seguridad.deserializacion-insegura", Pillar.Errores, "Deserialización insegura",
            "deserializadores que permiten tipos arbitrarios sobre entrada no confiable"),
        new("errores.seguridad.inyeccion", Pillar.Errores, "Inyección (SQL/command/path)",
            "concatenación de entrada no saneada en SQL, comandos de shell o rutas"),
        new("errores.concurrencia.race", Pillar.Errores, "Condición de carrera probable",
            "estado compartido mutado sin sincronización; comprobación-y-acción no atómica"),
        new("errores.null.desreferencia", Pillar.Errores, "Posible desreferencia nula",
            "acceso a valor potencialmente nulo sin comprobación"),
        new("errores.calculo.negocio", Pillar.Errores, "Error de cálculo de negocio",
            "redondeos, unidades, límites o fórmulas incorrectas en lógica de negocio"),

        // optimizacion
        new("optimizacion.io.sincrono-en-async", Pillar.Optimizacion, "I/O síncrono en ruta async",
            "llamadas bloqueantes de disco/red dentro de código asíncrono o de UI"),
        new("optimizacion.consulta.n-mas-1", Pillar.Optimizacion, "Consulta N+1",
            "consultas dentro de un bucle que podrían agruparse"),
        new("optimizacion.alloc.excesiva", Pillar.Optimizacion, "Asignación excesiva",
            "boxing, LINQ en caliente, concatenaciones o buffers recreados en bucles"),
        new("optimizacion.coleccion.ineficiente", Pillar.Optimizacion, "Estructura de datos ineficiente",
            "búsqueda lineal donde cabría un diccionario/conjunto; complejidad innecesaria"),

        // mejoras
        new("mejoras.mantenibilidad.unidad-grande", Pillar.Mejoras, "Unidad demasiado grande",
            "fichero/clase que excede el umbral y concentra demasiada responsabilidad"),
        new("mejoras.mantenibilidad.complejidad", Pillar.Mejoras, "Complejidad excesiva",
            "métodos largos, anidamiento profundo, complejidad ciclomática alta"),
        new("mejoras.modernizacion.api-obsoleta", Pillar.Mejoras, "API obsoleta",
            "uso de API deprecada o patrón anticuado con reemplazo idiomático disponible"),
        new("mejoras.estilo.nomenclatura", Pillar.Mejoras, "Nomenclatura/estilo",
            "nombres poco claros, inconsistencias de estilo, código muerto"),
    };

    /// <summary>Professional-judgement areas (§0): findings tagged <c>criterio</c> use these.</summary>
    public static IReadOnlyList<string> CriterioAreas { get; } = new[]
    {
        "criterio.arquitectura", "criterio.seguridad", "criterio.rendimiento",
        "criterio.testabilidad", "criterio.dominio", "criterio.observabilidad",
    };

    private static readonly HashSet<string> Ids =
        new(Rules.Select(r => r.RuleId).Concat(CriterioAreas), StringComparer.Ordinal);

    /// <summary>A ruleId is valid if it is a catalog rule or any <c>criterio.*</c> id (§6.2).</summary>
    public static bool IsValid(string ruleId)
        => Ids.Contains(ruleId) || ruleId.StartsWith("criterio.", StringComparison.Ordinal);

    public static RuleDef? Find(string ruleId) => Rules.FirstOrDefault(r => r.RuleId == ruleId);
}
