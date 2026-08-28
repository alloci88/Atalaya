namespace Atalaya.Inventory;

/// <summary>
/// Un patrón del catálogo de directivas (F7): dónde suele vivir un fichero de convenciones y qué
/// clase de material es.
/// </summary>
/// <param name="Kind">
/// La familia a la que pertenece (<c>agents</c>, <c>adr</c>, <c>skills</c>…). Agrupa la lista del
/// panel y viaja al registro del hub para explicar de dónde salió el candidato.
/// </param>
/// <param name="Glob">
/// Ruta relativa a la raíz del clon, con barras hacia delante. <c>**</c> vale por cero o más
/// segmentos; <c>*</c> por cualquier trozo dentro de un segmento.
/// </param>
/// <param name="Why">Por qué este patrón está en el catálogo. Se enseña en el panel.</param>
public sealed record DirectivePattern(string Kind, string Glob, string Why);

/// <summary>
/// Dónde buscar las directivas de un proyecto (F7). <b>Sitio único y ampliable</b>: añadir un
/// formato de instrucciones nuevo es añadir una línea aquí, y ninguna otra parte de la aplicación
/// conoce estas rutas.
/// <para>
/// El catálogo PROPONE; nunca dispone. Lo que encuentre son candidatos que aparecen sin activar
/// en el panel de gestión, y una persona decide cuáles son directivas de verdad y con qué ámbito.
/// La razón es que la misma ruta significa cosas distintas según el proyecto: un <c>specs/</c>
/// puede ser la especificación viva del producto o el cementerio de tres rediseños abandonados, y
/// eso no se distingue por la ruta.
/// </para>
/// <para>
/// Lo que el catálogo NO conozca se añade a mano desde el panel: es la válvula que impide que un
/// proyecto con sus propias costumbres se quede fuera esperando a que alguien amplíe esta lista.
/// </para>
/// </summary>
public static class DirectiveCatalog
{
    /// <summary>
    /// Extensiones que se aceptan cuando el patrón barre una CARPETA entera. Un <c>specs/</c> o un
    /// <c>.claude/skills/</c> traen capturas, diagramas y scripts junto al texto, y una directiva
    /// es TEXTO que se le enseña a un modelo: lo demás no es que estorbe, es que no se puede leer.
    /// Los patrones que nombran un fichero concreto no pasan por aquí — <c>.cursorrules</c> no
    /// tiene extensión y es una directiva de manual.
    /// </summary>
    public static IReadOnlyList<string> TextExtensions { get; } = new[]
    {
        ".md", ".mdc", ".markdown", ".txt", ".rst", ".adoc",
    };

    /// <summary>
    /// Tope de tamaño de un candidato, en bytes. No es un límite de la funcionalidad —el
    /// presupuesto de tokens ya recorta lo que viaja— sino del ESCANEO: leer y estimar un fichero
    /// de decenas de megas para ofrecerlo en una lista sería pagar un coste seguro por una utilidad
    /// nula. Un documento de convenciones de más de un mega no cabe en ningún prompt.
    /// </summary>
    public const long MaxCandidateBytes = 1_000_000;

    /// <summary>
    /// El catálogo. Cada patrón documenta a qué herramienta pertenece: quien lea esta lista dentro
    /// de dos años tiene que poder decidir si sobra sin salir del fichero.
    /// </summary>
    public static IReadOnlyList<DirectivePattern> Patterns { get; } = new DirectivePattern[]
    {
        // --- Instrucciones de agentes ---
        new("agents", "**/AGENTS.md",
            "Instrucciones de agente en el formato abierto AGENTS.md. Se busca en cualquier nivel: "
            + "en un monorepo cada paquete suele traer el suyo."),
        new("claude", "**/CLAUDE.md",
            "Instrucciones de Claude Code. Igual que AGENTS.md, se anida por carpetas."),
        new("copilot", ".github/copilot-instructions.md",
            "Instrucciones de repositorio de GitHub Copilot. Ruta fija por convención de la herramienta."),
        new("copilot", ".github/instructions/*.md",
            "Instrucciones de Copilot por área (*.instructions.md): un fichero por tema, todos en la misma carpeta."),
        new("cursor", ".cursorrules",
            "Reglas de Cursor en su formato antiguo: un único fichero en la raíz, sin extensión."),
        new("cursor", ".cursor/rules/**",
            "Reglas de Cursor en su formato actual (.mdc), una por fichero y con subcarpetas."),

        // --- Decisiones de arquitectura ---
        new("adr", "docs/adr/**",
            "Registros de decisión de arquitectura en su ubicación canónica (adr-tools, MADR)."),
        new("adr", "**/ADR-*.md",
            "ADRs sueltos, nombrados por convención, allá donde el proyecto los haya puesto."),

        // --- Especificaciones y producto ---
        new("prd", "**/PRD*.md",
            "Documentos de requisitos de producto. El prefijo es la convención más extendida."),
        new("spec", "specs/**",
            "Especificaciones del proyecto: lo que define el comportamiento pretendido en los "
            + "desarrollos spec-driven."),

        // --- Skills ---
        new("skills", ".claude/skills/**",
            "Colección de skills de Claude Code: procedimientos escritos del proyecto. Son TEXTO de "
            + "contexto — Atalaya no ejecuta nunca una skill ajena."),
    };

    /// <summary>
    /// Carpetas que el escaneo de directivas no pisa. <b>No son las exclusiones del inventario</b>:
    /// aquéllas podan <c>specs</c>, <c>tests</c> y <c>docs</c> porque no son código que auditar, y
    /// aquí son justo lo que se viene a buscar. Esta lista solo quita lo que nunca contiene
    /// directivas escritas por el equipo: dependencias, artefactos de compilación y metadatos.
    /// </summary>
    public static IReadOnlyList<string> PrunedDirectories { get; } = new[]
    {
        ".git", "node_modules", "vendor", "packages", "bin", "obj", "dist", "build",
        "target", "out", ".venv", "venv", "__pycache__", ".vs", ".idea", "coverage",
    };

    /// <summary>El primer patrón que reconoce esta ruta, o null si el catálogo no la conoce.</summary>
    public static DirectivePattern? Match(string relativePath)
    {
        foreach (DirectivePattern p in Patterns)
        {
            if (GlobPath.IsMatch(p.Glob, relativePath) && IsAcceptable(p, relativePath))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Un patrón que barre una carpeta (acaba en <c>**</c>) solo acepta ficheros de texto; uno que
    /// nombra un fichero acepta lo que nombra, tenga la extensión que tenga.
    /// </summary>
    private static bool IsAcceptable(DirectivePattern pattern, string relativePath)
    {
        if (!pattern.Glob.EndsWith("**", StringComparison.Ordinal))
        {
            return true;
        }

        string ext = Path.GetExtension(relativePath);
        return TextExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// El emparejador de rutas del catálogo (F7). Rutas relativas con barras hacia delante, insensible
/// a mayúsculas porque el sistema de ficheros de las máquinas del equipo lo es.
/// <para>
/// Se escribe aquí en lugar de estirar <c>ExclusionMatcher</c> a propósito: aquél empareja nombres
/// de fichero y segmentos sueltos —le basta para podar <c>bin</c>— y no sabe de rutas completas ni
/// de <c>**</c>. Ampliarlo habría hecho más difícil de entender la poda del inventario, que es de
/// las cosas que más caro sale equivocar.
/// </para>
/// </summary>
public static class GlobPath
{
    public static bool IsMatch(string glob, string relativePath)
    {
        string[] pattern = Split(glob);
        string[] path = Split(relativePath.Replace('\\', '/'));
        return Match(pattern, 0, path, 0);
    }

    private static string[] Split(string value)
        => value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Match(string[] pattern, int pi, string[] path, int si)
    {
        while (pi < pattern.Length)
        {
            if (pattern[pi] == "**")
            {
                // ** vale por CERO o más segmentos: «**/AGENTS.md» tiene que casar con «AGENTS.md»
                // en la raíz, que es donde está el 90 % de las veces.
                if (pi == pattern.Length - 1)
                {
                    return si <= path.Length;
                }

                for (int skip = si; skip <= path.Length; skip++)
                {
                    if (Match(pattern, pi + 1, path, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (si >= path.Length || !SegmentMatches(pattern[pi], path[si]))
            {
                return false;
            }

            pi++;
            si++;
        }

        return si == path.Length;
    }

    /// <summary>Un segmento con <c>*</c> por cualquier trozo, sin cruzar la barra.</summary>
    private static bool SegmentMatches(string pattern, string segment)
    {
        if (!pattern.Contains('*'))
        {
            return string.Equals(pattern, segment, StringComparison.OrdinalIgnoreCase);
        }

        string[] parts = pattern.Split('*');

        // El primer y el último trozo están anclados a los extremos; los de en medio solo tienen
        // que aparecer en orden.
        if (parts[0].Length > 0 && !segment.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parts[^1].Length > 0 && !segment.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Un patrón con un solo '*' ya está decidido por los dos anclajes, salvo que se solapen:
        // "ab*ba" no debe casar con "aba".
        int cursor = parts[0].Length;
        int limit = segment.Length - parts[^1].Length;
        if (limit < cursor)
        {
            return false;
        }

        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Length == 0)
            {
                continue;
            }

            int at = segment.IndexOf(parts[i], cursor, limit - cursor, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            cursor = at + parts[i].Length;
        }

        return true;
    }
}
