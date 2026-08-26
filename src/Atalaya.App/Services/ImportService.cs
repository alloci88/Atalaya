using Atalaya.Domain.Model;
using Atalaya.ImportV4;

namespace Atalaya.App.Services;

/// <summary>
/// Cómo se reconoce una carpeta <c>CodeAudit/</c> del sistema v4 (F5.9 §1).
/// <para>
/// El asistente de «Nueva aplicación» la busca solo, porque el sitio donde vive es siempre el
/// mismo —la raiz del repo que se acaba de elegir— y porque una acción de una-vez-por-app no
/// merecia un destino permanente en el menu. Si no esta ahí, el usuario la señala a mano.
/// </para>
/// </summary>
public static class V4Baseline
{
    /// <summary>El nombre de la carpeta en todos los repos del sistema anterior.</summary>
    public const string FolderName = "CodeAudit";

    /// <summary>
    /// Los ficheros del formato v4. Basta con UNO: un baseline sin histórico sigue siendo un
    /// baseline importable, y el importador ya es tolerante con lo que falte.
    /// </summary>
    private static readonly string[] Markers =
    {
        "BASELINE.md", "LOTES.md", "SILENCIADOS.md", "HISTORICO.md",
    };

    /// <summary>Esta carpeta ES una CodeAudit/ (tiene al menos uno de sus ficheros).</summary>
    public static bool Looks(string? dir)
        => !string.IsNullOrWhiteSpace(dir)
           && Directory.Exists(dir)
           && Markers.Any(m => File.Exists(Path.Combine(dir!, m)));

    /// <summary>
    /// Busca la carpeta dentro de un clon. Acepta que el usuario haya señalado directamente la
    /// propia <c>CodeAudit/</c> en vez de la raiz del repo: es el error facil de cometer y no
    /// tiene sentido rechazarlo cuando se sabe distinguir.
    /// </summary>
    public static string? Find(string? clonePath)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            return null;
        }

        string inside = Path.Combine(clonePath, FolderName);
        if (Looks(inside))
        {
            return inside;
        }

        return Looks(clonePath) ? clonePath : null;
    }
}

/// <summary>Wires the v4 importer (§9) into the hub: writes the parsed objects under a target app.</summary>
public sealed class ImportService
{
    private readonly HubContext _hub;

    public ImportService(HubContext hub) => _hub = hub;

    /// <summary>
    /// Importa el baseline v4 bajo <paramref name="slug"/>.
    /// </summary>
    /// <param name="push">
    /// F5.9 §1: el asistente de alta importa ANTES de escanear y publica una sola vez al final,
    /// así que le pasa <c>false</c>. Dos commits para un mismo gesto de alta no cuentan dos cosas
    /// distintas: cuentan la misma a medias.
    /// </param>
    public IReadOnlyList<string> Import(
        string slug, string appName, string repoUrl, string codeAuditDir, bool push = true)
    {
        ImportResult result = new V4Importer().Import(codeAuditDir);
        var log = new List<string>(result.Log);

        int cycle = result.Inventory?.CycleN ?? 1;
        AppConfig app = _hub.Store.TryReadApp(slug) ?? new AppConfig
        {
            Slug = slug,
            Name = appName,
            RepoUrl = repoUrl,
            CurrentCycle = cycle,
        };
        app.CurrentCycle = Math.Max(app.CurrentCycle, cycle);
        _hub.Store.WriteApp(app);

        foreach (Finding f in result.Findings)
        {
            TryWrite(() => _hub.Store.WriteFinding(slug, f), log, $"finding {f.DisplayId}");
        }

        foreach (Silence s in result.Silences)
        {
            TryWrite(() => _hub.Store.WriteSilence(slug, s), log, $"silence {s.FindingUlid}");
        }

        if (result.Inventory is not null)
        {
            TryWrite(() => _hub.Store.WriteInventory(slug, result.Inventory), log, "inventory");
        }

        foreach (AuditSession session in result.Sessions)
        {
            session.AppSlug = slug;
            TryWrite(() => _hub.Store.WriteSession(session), log, $"session {session.Id}");
        }

        foreach (ImportedReport report in result.Reports)
        {
            TryWrite(() => _hub.Store.WriteReport(slug, Path.GetFileNameWithoutExtension(report.Name), report.Content),
                log, $"report {report.Name}");
        }

        if (push)
        {
            _hub.Sync?.CommitAndPush($"import: v4 {slug} ({result.Findings.Count} hallazgos)");
        }

        log.Add($"Importados {result.Findings.Count} hallazgos, {result.Silences.Count} silencios, {result.Sessions.Count} sesiones.");
        return log;
    }

    private static void TryWrite(Action write, List<string> log, string what)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            log.Add($"No importable ({what}): {ex.Message}");
        }
    }
}
