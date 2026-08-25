using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.ImportV4;
using FluentAssertions;
using Xunit;

namespace Atalaya.ImportV4.Tests;

public class ImportTests
{
    private static ImportResult Import()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "CodeAudit");
        return new V4Importer().Import(dir);
    }

    [Fact]
    public void Imports_all_ten_findings_with_pillar_and_severity()
    {
        ImportResult r = Import();

        r.Findings.Should().HaveCount(10);
        r.Findings.Count(f => f.Pillar == Pillar.Errores).Should().Be(4);
        r.Findings.Count(f => f.Pillar == Pillar.Optimizacion).Should().Be(3);
        r.Findings.Count(f => f.Pillar == Pillar.Mejoras).Should().Be(3);

        Finding bug1 = r.Findings.Single(f => f.DisplayId == "BUG-0001");
        bug1.Severity.Should().Be(Severity.Critica);
        bug1.Confidence.Should().Be(Confidence.Alta);
        bug1.TimesConfirmed.Should().Be(3);
        bug1.Locations.Single().Path.Should().Be("src/Db/Pool.cs");
        bug1.Locations.Single().Line.Should().Be(120);
    }

    [Fact]
    public void Imports_two_silences_and_marks_findings_silenced()
    {
        ImportResult r = Import();

        r.Silences.Should().HaveCount(2);
        r.Silences.Should().Contain(s => s.Reason == SilenceReason.DeudaAceptada);
        r.Silences.Should().Contain(s => s.Reason == SilenceReason.FalsoPositivo);

        // The corresponding findings are silenced and the silence points at the finding's ULID.
        Finding bug2 = r.Findings.Single(f => f.DisplayId == "BUG-0002");
        bug2.Status.Should().Be(FindingStatus.Silenciado);
        r.Silences.Should().Contain(s => s.FindingUlid == bug2.Id);
    }

    [Fact]
    public void Imports_half_done_cycle_inventory_with_states()
    {
        ImportResult r = Import();

        r.Inventory.Should().NotBeNull();
        r.Inventory!.CycleN.Should().Be(2);
        r.Inventory.Units.Count(u => u.State == UnitState.Auditada).Should().Be(3);
        r.Inventory.Units.Count(u => u.State == UnitState.Grande).Should().Be(1);
        // "[ ]" and "[~]" both import as pending.
        r.Inventory.Units.Count(u => u.State == UnitState.Pendiente).Should().Be(4);
    }

    [Fact]
    public void Imports_history_sessions_and_copies_reports()
    {
        ImportResult r = Import();

        r.Sessions.Should().HaveCount(3); // the corrupt line is skipped
        r.Sessions.Should().Contain(s => s.Mode == AuditMode.Integral && s.By == "maria");
        r.Reports.Should().ContainSingle().Which.Name.Should().Be("2026-02-01-integral.md");
    }

    [Fact]
    public void Corrupt_history_line_is_logged_not_fatal()
    {
        ImportResult r = Import();
        r.Findings.Should().NotBeEmpty(); // whole import still succeeded
    }

    [Fact]
    public void Missing_folder_does_not_throw()
    {
        ImportResult r = new V4Importer().Import(Path.Combine(AppContext.BaseDirectory, "does-not-exist"));
        r.Findings.Should().BeEmpty();
        r.Log.Should().NotBeEmpty();
    }
}
