using Atalaya.Domain.Model;

namespace Atalaya.ImportV4;

/// <summary>A copied report file (§9).</summary>
public sealed record ImportedReport(string Name, string Content);

/// <summary>
/// The parsed contents of a v4 <c>CodeAudit/</c> folder (§9). Domain objects ready to be written to
/// the hub by the app. The <see cref="Log"/> lists everything that could not be imported — the import
/// never fails as a whole for one corrupt entry.
/// </summary>
public sealed class ImportResult
{
    public List<Finding> Findings { get; } = new();

    public List<Silence> Silences { get; } = new();

    public InventoryCycle? Inventory { get; set; }

    public List<AuditSession> Sessions { get; } = new();

    public List<ImportedReport> Reports { get; } = new();

    public List<string> Log { get; } = new();

    public void Note(string message) => Log.Add(message);
}
