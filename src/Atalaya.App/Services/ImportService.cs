using Atalaya.Domain.Model;
using Atalaya.ImportV4;

namespace Atalaya.App.Services;

/// <summary>Wires the v4 importer (§9) into the hub: writes the parsed objects under a target app.</summary>
public sealed class ImportService
{
    private readonly HubContext _hub;

    public ImportService(HubContext hub) => _hub = hub;

    public IReadOnlyList<string> Import(string slug, string appName, string repoUrl, string codeAuditDir)
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
            TryWrite(() => _hub.Store.WriteSilence(slug, s), log, $"silence {s.Fingerprint}");
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

        _hub.Sync?.CommitAndPush($"import: v4 {slug} ({result.Findings.Count} hallazgos)");
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
