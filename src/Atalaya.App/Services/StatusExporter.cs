using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// Courtesy export (§7): writes a generated, read-only <c>ESTADO.md</c> into the audited app's
/// clone when <c>app.exportStatusMd</c> is on. This is the only file Atalaya writes into the app
/// repo, and it is generated — never the internal format (§12).
/// </summary>
public sealed class StatusExporter
{
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;

    public StatusExporter(HubContext hub, MachineConfigStore machines)
    {
        _hub = hub;
        _machines = machines;
    }

    public bool ExportIfEnabled(string slug)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null || !app.ExportStatusMd)
        {
            return false;
        }

        string? clone = _machines.Load().ClonePathFor(slug);
        if (string.IsNullOrWhiteSpace(clone) || !Directory.Exists(clone))
        {
            return false;
        }

        var active = _hub.Store.ListFindings(slug).Where(f => f.Status == FindingStatus.Activo).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# ESTADO de auditoría — {app.Name}");
        sb.AppendLine();
        sb.AppendLine("> Fichero generado por Atalaya. No editar a mano.");
        sb.AppendLine();
        sb.AppendLine($"- Ciclo actual: {app.CurrentCycle}");
        sb.AppendLine($"- Hallazgos activos: {active.Count}");
        foreach (Severity sev in Enum.GetValues<Severity>())
        {
            sb.AppendLine($"  - {sev}: {active.Count(f => f.Severity == sev)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Prioridades");
        foreach (Finding f in active.OrderBy(f => f.Severity).ThenBy(f => f.Confidence).Take(15))
        {
            string loc = f.Locations.Count > 0 ? $"{f.Locations[0].Path}:{f.Locations[0].Line}" : "";
            sb.AppendLine($"- **[{f.Severity}]** {f.Title} — `{f.RuleId}` {loc}");
        }

        string path = Path.Combine(clone, "ESTADO.md");
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.WriteAllText(path, sb.ToString());
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
