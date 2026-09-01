using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.Storage;

/// <summary>
/// Reads and writes the hub's primary JSON files (§2), validating against the schema on
/// both read and write. Knows nothing about git — <see cref="Sync.HubSyncService"/> owns
/// pull/commit/push around these operations. Dashboards are never stored (mejora 8): this
/// only persists primary data.
/// </summary>
public sealed class HubStore
{
    private readonly HubPaths _paths;
    private readonly ILogger<HubStore> _log;

    public HubStore(HubPaths paths, ILogger<HubStore>? log = null)
    {
        _paths = paths;
        _log = log ?? NullLogger<HubStore>.Instance;
    }

    public HubPaths Paths => _paths;

    // --- Hub root ---

    public HubInfo? TryReadHub()
        => File.Exists(_paths.HubJson) ? ReadJson<HubInfo>(_paths.HubJson, SchemaValidation.Validate) : null;

    public void WriteHub(HubInfo hub) => WriteJson(_paths.HubJson, hub, SchemaValidation.Validate);

    // --- Tarifas por modelo (F15) ---

    /// <summary>
    /// La tabla de tarifas de la organización, o null si todavía no se ha sembrado. Null NO se
    /// convierte en una tabla vacía a la ligera: «no hay tabla» y «hay tabla y está vacía» son
    /// estados distintos, y quien pregunta tiene que poder ofrecer sembrarla.
    /// </summary>
    public ModelRateTable? TryReadModelRates()
        => File.Exists(_paths.ModelRatesJson)
            ? ReadJson<ModelRateTable>(_paths.ModelRatesJson, SchemaValidation.Validate)
            : null;

    public void WriteModelRates(ModelRateTable rates)
        => WriteJson(_paths.ModelRatesJson, rates, SchemaValidation.Validate);

    // --- Apps ---

    public IReadOnlyList<string> ListAppSlugs()
    {
        if (!Directory.Exists(_paths.AppsDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(_paths.AppsDir)
            .Where(d => File.Exists(Path.Combine(d, "app.json")))
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    public AppConfig? TryReadApp(string slug)
        => File.Exists(_paths.AppJson(slug)) ? ReadJson<AppConfig>(_paths.AppJson(slug), SchemaValidation.Validate) : null;

    public void WriteApp(AppConfig app) => WriteJson(_paths.AppJson(app.Slug), app, SchemaValidation.Validate);

    /// <summary>
    /// Borra la carpeta ENTERA de una app: hallazgos, sesiones, informes, inventario, silencios,
    /// claims, comentarios y <c>app.json</c> (F5.3 §4). Devuelve false si no estaba.
    /// <para>
    /// Borra el directorio en vez de ir fichero a fichero a propósito: enumerar por tipo dejaría
    /// fuera cualquier cosa que se añada al esquema más adelante, y un hard-reset que se deja
    /// medio rastro no es un hard-reset. Lo recuperable es el historial de git, no lo que quede
    /// suelto en el árbol de trabajo.
    /// </para>
    /// </summary>
    public bool DeleteApp(string slug)
    {
        string dir = _paths.AppDir(slug);
        if (!Directory.Exists(dir))
        {
            return false;
        }

        Directory.Delete(dir, recursive: true);
        return true;
    }

    // --- Inventory ---

    public InventoryCycle? TryReadInventory(string slug, int cycleN)
        => File.Exists(_paths.InventoryFile(slug, cycleN))
            ? ReadJson<InventoryCycle>(_paths.InventoryFile(slug, cycleN), SchemaValidation.Validate)
            : null;

    public void WriteInventory(string slug, InventoryCycle inventory)
        => WriteJson(_paths.InventoryFile(slug, inventory.CycleN), inventory, SchemaValidation.Validate);

    // --- Findings ---

    public IReadOnlyList<Finding> ListFindings(string slug)
        => ReadAll<Finding>(_paths.FindingsDir(slug), SchemaValidation.Validate);

    public Finding? TryReadFinding(string slug, string ulid)
        => File.Exists(_paths.FindingFile(slug, ulid))
            ? ReadJson<Finding>(_paths.FindingFile(slug, ulid), SchemaValidation.Validate)
            : null;

    public void WriteFinding(string slug, Finding finding)
        => WriteJson(_paths.FindingFile(slug, finding.Id.ToString()), finding, SchemaValidation.Validate);

    // --- Silences ---

    public IReadOnlyList<Silence> ListSilences(string slug)
        => ReadAll<Silence>(_paths.SilencesDir(slug), SchemaValidation.Validate);

    /// <summary>F4: el silencio se busca por el ULID del hallazgo, no por fingerprint.</summary>
    public Silence? TryReadSilence(string slug, Ulid findingUlid)
        => File.Exists(_paths.SilenceFile(slug, findingUlid.ToString()))
            ? ReadJson<Silence>(_paths.SilenceFile(slug, findingUlid.ToString()), SchemaValidation.Validate)
            : null;

    public void WriteSilence(string slug, Silence silence)
        => WriteJson(_paths.SilenceFile(slug, silence.FindingUlid.ToString()), silence, SchemaValidation.Validate);

    public bool DeleteSilence(string slug, Ulid findingUlid)
    {
        string path = _paths.SilenceFile(slug, findingUlid.ToString());
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    // --- Pattern silences (F5.12) ---

    /// <summary>
    /// Los patrones silenciados de esta app, VIVOS Y CADUCADOS. El filtrado por caducidad es de
    /// quien pregunta (<see cref="Domain.Model.PatternSilenceSet"/>), porque la gestión necesita
    /// ver los caducados para poder decir «caducado — revisar».
    /// </summary>
    public IReadOnlyList<PatternSilence> ListPatternSilences(string slug)
        => ReadAll<PatternSilence>(_paths.PatternSilencesDir(slug), SchemaValidation.Validate);

    public PatternSilence? TryReadPatternSilence(string slug, Ulid patternId)
        => File.Exists(_paths.PatternSilenceFile(slug, patternId.ToString()))
            ? ReadJson<PatternSilence>(_paths.PatternSilenceFile(slug, patternId.ToString()), SchemaValidation.Validate)
            : null;

    public void WritePatternSilence(string slug, PatternSilence pattern)
        => WriteJson(_paths.PatternSilenceFile(slug, pattern.Id.ToString()), pattern, SchemaValidation.Validate);

    public bool DeletePatternSilence(string slug, Ulid patternId)
    {
        string path = _paths.PatternSilenceFile(slug, patternId.ToString());
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    // --- Project directives (F7) ---

    /// <summary>
    /// El registro de directivas de esta app: rutas, ámbitos y quién las marcó. Incluye las
    /// entradas con ámbito <c>Ninguno</c> —candidatos vistos y descartados—, porque el panel
    /// necesita distinguir «esto no lo ha mirado nadie» de «esto se miró y se dejó fuera».
    /// </summary>
    public IReadOnlyList<ProjectDirective> ListDirectives(string slug)
        => ReadAll<ProjectDirective>(_paths.DirectivesDir(slug), SchemaValidation.Validate);

    public ProjectDirective? TryReadDirective(string slug, Ulid id)
        => File.Exists(_paths.DirectiveFile(slug, id.ToString()))
            ? ReadJson<ProjectDirective>(_paths.DirectiveFile(slug, id.ToString()), SchemaValidation.Validate)
            : null;

    public void WriteDirective(string slug, ProjectDirective directive)
        => WriteJson(_paths.DirectiveFile(slug, directive.Id.ToString()), directive, SchemaValidation.Validate);

    public bool DeleteDirective(string slug, Ulid id)
    {
        string path = _paths.DirectiveFile(slug, id.ToString());
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    // --- Fix records (F9 §2) ---

    /// <summary>
    /// Lo que los arreglos de esta app dejaron escrito. Es lo que impide que un arreglo se cuente
    /// como deriva ajena y realimente la lista de unidades cambiadas.
    /// </summary>
    public IReadOnlyList<FixRecord> ListFixes(string slug)
        => ReadAll<FixRecord>(_paths.FixesDir(slug), SchemaValidation.Validate);

    public void WriteFix(FixRecord record)
        => WriteJson(_paths.FixFile(record.AppSlug, record.Id.ToString()), record, SchemaValidation.Validate);

    // --- Claims (deleted on release, §2) ---

    public IReadOnlyList<Claim> ListClaims(string slug)
        => ReadAll<Claim>(_paths.ClaimsDir(slug), SchemaValidation.Validate);

    public Claim? TryReadClaim(string slug, string unitHash)
        => File.Exists(_paths.ClaimFile(slug, unitHash))
            ? ReadJson<Claim>(_paths.ClaimFile(slug, unitHash), SchemaValidation.Validate)
            : null;

    public void WriteClaim(string slug, Claim claim)
        => WriteJson(_paths.ClaimFile(slug, claim.UnitHash), claim, SchemaValidation.Validate);

    public bool DeleteClaim(string slug, string unitHash)
    {
        string path = _paths.ClaimFile(slug, unitHash);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    // --- Sessions (append-only, immutable) ---

    public IReadOnlyList<AuditSession> ListSessions(string slug)
        => ReadAll<AuditSession>(_paths.SessionsDir(slug), SchemaValidation.Validate);

    public void WriteSession(AuditSession session)
        => WriteJson(_paths.SessionFile(session.AppSlug, session.Id.ToString()), session, SchemaValidation.Validate);

    // --- Comments ---

    public IReadOnlyList<Comment> ListComments(string slug, string findingUlid)
        => ReadAll<Comment>(_paths.CommentThreadDir(slug, findingUlid), SchemaValidation.Validate);

    public void WriteComment(string slug, Comment comment)
        => WriteJson(_paths.CommentFile(slug, comment.FindingUlid.ToString(), comment.Id.ToString()),
            comment, SchemaValidation.Validate);

    // --- Reports (immutable markdown) ---

    /// <summary>Los informes publicados de una app: markdown inmutable, uno por sesión.</summary>
    public IReadOnlyList<string> ListReports(string slug)
        => Directory.Exists(_paths.ReportsDir(slug))
            ? Directory.EnumerateFiles(_paths.ReportsDir(slug), "*.md", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList()
            : Array.Empty<string>();

    public void WriteReport(string slug, string sessionUlid, string markdown)
    {
        string path = _paths.ReportFile(slug, sessionUlid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown.Replace("\r\n", "\n"));
    }

    // --- helpers ---

    private static T ReadJson<T>(string path, Action<T> validate)
    {
        T value = AtalayaJson.Deserialize<T>(File.ReadAllText(path));
        validate(value);
        return value;
    }

    private static void WriteJson<T>(string path, T value, Action<T> validate)
    {
        validate(value);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, AtalayaJson.Serialize(value));
    }

    /// <summary>Reads every *.json in a directory, skipping (and logging) any corrupt file.</summary>
    private List<T> ReadAll<T>(string dir, Action<T> validate)
    {
        var result = new List<T>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                result.Add(ReadJson(file, validate));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping unreadable hub file {File}", file);
            }
        }

        return result;
    }
}
