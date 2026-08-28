using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.Storage;

/// <summary>Thrown when an entity fails schema validation on read or write (§2).</summary>
public sealed class SchemaValidationException : Exception
{
    public SchemaValidationException(string message) : base(message) { }
}

/// <summary>
/// Validates entities against the §2 schema on both read and write. Enum membership is
/// already guaranteed by the JSON converters; this catches structural invariants
/// (required non-empty fields, ranges, prefixes, referential shape).
/// </summary>
public static class SchemaValidation
{
    private const int CurrentSchemaVersion = 1;

    public static void Validate(Finding f)
    {
        Require(f.SchemaVersion == CurrentSchemaVersion, "finding.schemaVersion must be 1");
        Require(f.Id != Ulid.Empty, "finding.id must be a non-empty ULID");
        RequireText(f.RuleId, "finding.ruleId");
        RequireText(f.Title, "finding.title");
        Require(f.Locations.Count > 0, "finding.locations must have at least one entry");
        foreach (Location loc in f.Locations)
        {
            RequireText(loc.Path, "finding.locations[].path");
            Require(loc.Line >= 0, "finding.locations[].line must be >= 0");
        }

        Require(f.TimesConfirmed >= 1, "finding.timesConfirmed must be >= 1");
        Require(f.Status != FindingStatus.Resuelto || f.Resolved is not null,
            "a resolved finding must carry a resolution stamp");
    }

    public static void Validate(Silence s)
    {
        Require(s.SchemaVersion == CurrentSchemaVersion, "silence.schemaVersion must be 1");
        Require(s.FindingUlid != Ulid.Empty, "silence.findingUlid must be a non-empty ULID");
        RequireText(s.By, "silence.by");
    }

    /// <summary>
    /// F5.12. El <c>exemplar</c> es obligatorio y no vacío porque ES el alcance: un patrón sin
    /// frase no le dice nada al auditor y se traduciría en supresiones que nadie puede explicar.
    /// </summary>
    public static void Validate(PatternSilence p)
    {
        Require(p.SchemaVersion == CurrentSchemaVersion, "patternSilence.schemaVersion must be 1");
        Require(p.Id != Ulid.Empty, "patternSilence.id must be a non-empty ULID");
        RequireText(p.ShortId, "patternSilence.shortId");
        RequireText(p.Exemplar, "patternSilence.exemplar");
        RequireText(p.By, "patternSilence.by");
        Require(p.Suppressions >= 0, "patternSilence.suppressions must be >= 0");
    }

    /// <summary>
    /// F7. La <c>path</c> es obligatoria porque ES la directiva: sin ella la entrada no apunta a
    /// nada del repo. El ámbito no se valida —<c>Ninguno</c> es un estado legítimo: el candidato
    /// que alguien miró y decidió no activar—.
    /// </summary>
    public static void Validate(ProjectDirective d)
    {
        Require(d.SchemaVersion == CurrentSchemaVersion, "directive.schemaVersion must be 1");
        Require(d.Id != Ulid.Empty, "directive.id must be a non-empty ULID");
        RequireText(d.Path, "directive.path");
        RequireText(d.Kind, "directive.kind");
        RequireText(d.By, "directive.by");
    }

    public static void Validate(Claim c)
    {
        Require(c.SchemaVersion == CurrentSchemaVersion, "claim.schemaVersion must be 1");
        RequireText(c.Unit, "claim.unit");
        RequireText(c.By, "claim.by");
        RequireText(c.Machine, "claim.machine");
        Require(c.TtlMinutes > 0, "claim.ttlMinutes must be > 0");
    }

    public static void Validate(AuditSession s)
    {
        Require(s.SchemaVersion == CurrentSchemaVersion, "session.schemaVersion must be 1");
        Require(s.Id != Ulid.Empty, "session.id must be a non-empty ULID");
        RequireText(s.AppSlug, "session.appSlug");
        RequireText(s.By, "session.by");
        RequireText(s.Machine, "session.machine");
    }

    public static void Validate(InventoryCycle inv)
    {
        Require(inv.SchemaVersion == CurrentSchemaVersion, "inventory.schemaVersion must be 1");
        Require(inv.CycleN >= 1, "inventory.cycleN must be >= 1");
        foreach (InventoryUnit u in inv.Units)
        {
            RequireText(u.Path, "inventory.units[].path");
            RequireText(u.Module, "inventory.units[].module");
            Require(u.Loc >= 0, "inventory.units[].loc must be >= 0");
        }
    }

    public static void Validate(AppConfig a)
    {
        Require(a.SchemaVersion == CurrentSchemaVersion, "app.schemaVersion must be 1");
        RequireText(a.Slug, "app.slug");
        RequireText(a.Name, "app.name");
        RequireText(a.RepoUrl, "app.repoUrl");
        Require(a.CurrentCycle >= 1, "app.currentCycle must be >= 1");
    }

    public static void Validate(HubInfo h)
    {
        Require(h.SchemaVersion == CurrentSchemaVersion, "hub.schemaVersion must be 1");
        RequireText(h.OrganizationName, "hub.organizationName");
    }

    public static void Validate(Comment c)
    {
        Require(c.SchemaVersion == CurrentSchemaVersion, "comment.schemaVersion must be 1");
        Require(c.Id != Ulid.Empty, "comment.id must be a non-empty ULID");
        RequireText(c.By, "comment.by");
        RequireText(c.Body, "comment.body");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new SchemaValidationException(message);
        }
    }

    private static void RequireText(string? value, string field)
        => Require(!string.IsNullOrWhiteSpace(value), $"{field} must be non-empty");
}
