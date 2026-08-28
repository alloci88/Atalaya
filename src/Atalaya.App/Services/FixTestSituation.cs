namespace Atalaya.App.Services;

/// <summary>
/// Si el código que se va a arreglar tiene tests, y cuáles — resuelto por la APLICACIÓN antes de
/// abrir la sesión (H9.1 §3).
/// <para>
/// <b>Por qué lo hace la app y no el agente.</b> En el primer uso real el agente gastó turnos
/// buscando un proyecto de tests que no existe y acabó declarando como riesgo que «no se localizó
/// un proyecto de tests accesible en las rutas convencionales». Buena parte de los proyectos de la
/// organización no tienen tests, y esto es un dato del repositorio que se resuelve leyendo dos
/// ficheros: dejar que lo averigüe el modelo, turno a turno y a ciegas, es tirar tokens para llegar
/// a lo que ya se sabía.
/// </para>
/// <para>
/// La detección es la misma de <see cref="BuildScopeResolver"/> —el proyecto dueño del fichero y
/// los que le apuntan con un <c>ProjectReference</c> declarándose de test—, no una segunda regla
/// paralela. Las exclusiones del inventario (<c>tests/</c>, <c>*Tests.cs</c>) son patrones de RUTA
/// para no auditar código de test: sirven para otra cosa y no distinguen un proyecto de un
/// directorio, así que no se reutilizan aquí.
/// </para>
/// </summary>
/// <param name="Project">El proyecto afectado, en ruta relativa. Null si lo tocado no cae en uno.</param>
/// <param name="TestProjects">Los proyectos de test que lo cubren, en rutas relativas.</param>
/// <param name="AnyInClone">Hay ALGÚN proyecto de test en el clon, aunque no cubra a este.</param>
public sealed record FixTestSituation(
    string? Project, IReadOnlyList<string> TestProjects, bool AnyInClone)
{
    /// <summary>Cuando no se pudo mirar el clon: ni se afirma que hay tests ni que no los hay.</summary>
    public static FixTestSituation Unknown { get; } = new(null, Array.Empty<string>(), AnyInClone: true);

    public bool HasTests => TestProjects.Count > 0;

    /// <summary>
    /// La línea que va en el encargo del agente. En los dos sentidos es una AFIRMACIÓN, no una
    /// sugerencia de por dónde buscar: o los tests están en tal proyecto, o no hay y no se buscan.
    /// </summary>
    public string PromptLine
    {
        get
        {
            if (HasTests)
            {
                return $"El proyecto afectado ({Project}) tiene tests en "
                    + $"{string.Join(", ", TestProjects)}; `run_build_and_tests` los ejecutará.";
            }

            return AnyInClone
                ? $"**El proyecto afectado ({Project ?? "el que estás tocando"}) no tiene proyecto "
                  + "de tests que lo cubra.** No lo busques ni lo crees salvo que el usuario te lo "
                  + "pida: verifica tu cambio con la compilación y con el análisis del código."
                : "**Esta solución no tiene proyectos de tests. No los busques ni los crees** salvo "
                  + "petición explícita del usuario; verifica tu cambio con la compilación y el "
                  + "análisis del código.";
        }
    }

    /// <summary>Lo que se narra en la conversación al arrancar. Un hecho del proyecto, sin drama.</summary>
    public string Narration => HasTests
        ? $"Tests del proyecto afectado: {string.Join(", ", TestProjects)}. Se pasarán al compilar."
        : AnyInClone
            ? $"El proyecto afectado no tiene tests que lo cubran. El agente lo sabe y no los buscará."
            : "Esta solución no tiene proyectos de tests. El agente lo sabe y no los buscará.";

    /// <summary>
    /// Resuelve la situación mirando el clon. <paramref name="paths"/> son las ubicaciones del
    /// hallazgo: el proyecto afectado es el de esos ficheros, que es el que se va a compilar.
    /// </summary>
    public static FixTestSituation Detect(string? cloneRoot, IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(cloneRoot) || !Directory.Exists(cloneRoot))
        {
            return Unknown;
        }

        var files = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        IReadOnlyList<string> projects = BuildScopeResolver.AllProjects(cloneRoot);
        bool any = projects.Any(p => !BuildScopeResolver.IsForeign(p) && BuildScopeResolver.IsTestProject(p));

        BuildPlan plan = BuildScopeResolver.Resolve(cloneRoot, files, fullSolution: false);
        if (plan.Target.Kind != BuildTargetKind.Project)
        {
            return new FixTestSituation(null, Array.Empty<string>(), any);
        }

        return new FixTestSituation(
            plan.Target.Relative,
            plan.TestProjects.Select(p => BuildScopeResolver.Relative(cloneRoot, p)).ToList(),
            any);
    }
}
