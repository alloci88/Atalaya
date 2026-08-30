namespace Atalaya.Storage;

/// <summary>
/// Builds the on-disk paths of the audit-hub layout (§2). All state is small JSON files
/// with unique names so two users almost never write the same file.
/// </summary>
public sealed class HubPaths
{
    public HubPaths(string root) => Root = Path.GetFullPath(root);

    /// <summary>The hub working directory (a git clone).</summary>
    public string Root { get; }

    public string HubJson => Path.Combine(Root, "hub.json");

    public string AppsDir => Path.Combine(Root, "apps");

    public string AppDir(string slug) => Path.Combine(AppsDir, slug);

    public string AppJson(string slug) => Path.Combine(AppDir(slug), "app.json");

    public string InventoryDir(string slug) => Path.Combine(AppDir(slug), "inventory");

    public string InventoryFile(string slug, int cycleN) => Path.Combine(InventoryDir(slug), $"cycle{cycleN}.json");

    public string FindingsDir(string slug) => Path.Combine(AppDir(slug), "findings");

    public string FindingFile(string slug, string ulid) => Path.Combine(FindingsDir(slug), $"{ulid}.json");

    public string SilencesDir(string slug) => Path.Combine(AppDir(slug), "silences");

    /// <summary>F4: un silencio se nombra por el ULID del hallazgo que silencia.</summary>
    public string SilenceFile(string slug, string findingUlid) => Path.Combine(SilencesDir(slug), $"{findingUlid}.json");

    /// <summary>
    /// F5.12: los TIPOS de problema silenciados en ESTA app. Un fichero por patrón, merge-friendly.
    /// <para>
    /// Carpeta propia y no <c>silences/</c> aunque el patrón sea «un silencio con otro alcance»:
    /// la clave de un silencio es el ULID del HALLAZGO y la de un patrón es la suya propia, así que
    /// compartir carpeta obligaría a todo lector de <c>silences/</c> —incluida la migración de F4,
    /// que enumera todos sus ficheros— a conocer un discriminador para siempre. Un hallazgo puede
    /// además estar silenciado por sí mismo Y ser el origen de un patrón, y los dos ficheros no
    /// pueden llamarse igual.
    /// </para>
    /// </summary>
    public string PatternSilencesDir(string slug) => Path.Combine(AppDir(slug), "pattern-silences");

    /// <summary>
    /// <c>apps/{slug}/pattern-silences/{ulid}.json</c>. La clave es el ULID del patrón, generado
    /// por la app: no hay aquí ningún texto que venga del modelo, así que no hay ruta que escapar.
    /// </summary>
    public string PatternSilenceFile(string slug, string patternUlid)
        => Path.Combine(PatternSilencesDir(slug), $"{patternUlid}.json");

    /// <summary>
    /// F7: el REGISTRO de qué ficheros del repo de esta app son directivas del proyecto. Un
    /// fichero por directiva, merge-friendly como todo lo demás.
    /// <para>
    /// Aquí no hay contenido de ninguna directiva y no lo habrá nunca: las directivas viven
    /// versionadas en el repo de la aplicación, y el hub solo registra cuáles son. Sincronizar el
    /// texto sería crear una segunda verdad que empieza a envejecer el mismo día que se escribe.
    /// </para>
    /// </summary>
    public string DirectivesDir(string slug) => Path.Combine(AppDir(slug), "directives");

    /// <summary>
    /// <c>apps/{slug}/directives/{ulid}.json</c>. La clave es el ULID de la entrada y no la ruta
    /// del fichero: una ruta lleva barras, puntos y mayúsculas: convertirla en nombre de fichero
    /// obligaría a escapar, y renombrar el fichero en el repo de la app obligaría a mover un
    /// fichero del hub, perdiendo de paso quién y cuándo lo marcó.
    /// </summary>
    public string DirectiveFile(string slug, string ulid)
        => Path.Combine(DirectivesDir(slug), $"{ulid}.json");

    /// <summary>Legado de F5.10, solo para que la migración a patrones sepa dónde mirar.</summary>
    public string LegacyRuleExclusionsDir(string slug) => Path.Combine(AppDir(slug), "rule-exclusions");

    /// <summary>
    /// F9 §2: lo que los arreglos de la propia aplicación dejaron escrito en el clon. Un fichero por
    /// arreglo, nombrado por el ULID de su sesión <c>fix</c>, merge-friendly como todo lo demás.
    /// <para>
    /// Carpeta propia y no dentro de <c>sessions/</c> porque una sesión es un registro INMUTABLE de
    /// lo que pasó y esto es una huella que se CONSULTA en cada cálculo de deriva: mezclarlos
    /// obligaría a leer y validar todas las sesiones de la app para responder una pregunta sobre
    /// arreglos, que son un puñado.
    /// </para>
    /// </summary>
    public string FixesDir(string slug) => Path.Combine(AppDir(slug), "fixes");

    /// <summary><c>apps/{slug}/fixes/{ulid}.json</c>, con el ULID de la sesión de arreglo.</summary>
    public string FixFile(string slug, string sessionUlid) => Path.Combine(FixesDir(slug), $"{sessionUlid}.json");

    public string ClaimsDir(string slug) => Path.Combine(AppDir(slug), "claims");

    public string ClaimFile(string slug, string unitHash) => Path.Combine(ClaimsDir(slug), $"{HashToFileName(unitHash)}.json");

    public string SessionsDir(string slug) => Path.Combine(AppDir(slug), "sessions");

    public string SessionFile(string slug, string ulid) => Path.Combine(SessionsDir(slug), $"{ulid}.json");

    public string CommentsDir(string slug) => Path.Combine(AppDir(slug), "comments");

    public string CommentThreadDir(string slug, string findingUlid) => Path.Combine(CommentsDir(slug), findingUlid);

    public string CommentFile(string slug, string findingUlid, string commentUlid)
        => Path.Combine(CommentThreadDir(slug, findingUlid), $"{commentUlid}.json");

    public string ReportsDir(string slug) => Path.Combine(AppDir(slug), "reports");

    public string ReportFile(string slug, string sessionUlid) => Path.Combine(ReportsDir(slug), $"{sessionUlid}.md");

    /// <summary>Strips the <c>sha256:</c> prefix to get a filesystem-safe name.</summary>
    public static string HashToFileName(string hashWithPrefix)
    {
        int colon = hashWithPrefix.IndexOf(':');
        return colon >= 0 ? hashWithPrefix[(colon + 1)..] : hashWithPrefix;
    }

    /// <summary>The repo-relative path of a file (forward slashes), for git operations.</summary>
    public string RelativeOf(string absolutePath)
        => Path.GetRelativePath(Root, absolutePath).Replace('\\', '/');
}
