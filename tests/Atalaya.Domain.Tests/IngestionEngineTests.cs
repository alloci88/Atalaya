using Atalaya.Domain;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

public class IngestionEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);

    private static SubmittedFinding Sample(string ruleId = "errores.recursos.no-liberado",
        string path = "src/Db/Pool.cs", string? symbol = "Pool.Acquire", string title = "Conn leaked")
        => new(ruleId, Pillar.Errores, FindingTag.Criterio, Severity.Critica,
            title, "desc", "impact", "reco",
            new[] { new Location(path, 120, "sha256:snip") }, symbol);

    private static DetectionStamp Stamp(AuditMode mode, string by = "alvaro", string commit = "abc1234")
        => new(T0, mode, commit, by);

    private static IngestionEngine NewEngine() => new(new TestUlidFactory());

    [Fact]
    public void New_finding_is_created_with_birth_confidence()
    {
        var outcome = NewEngine().Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            existingSameFingerprint: Array.Empty<Finding>(), liveSilence: null);

        outcome.Kind.Should().Be(IngestionKind.New);
        outcome.Finding!.Confidence.Should().Be(Confidence.Media); // lotes → media
        outcome.Finding.Status.Should().Be(FindingStatus.Activo);
        outcome.Finding.TimesConfirmed.Should().Be(1);
        outcome.Finding.History.Should().ContainSingle(h => h.Event == FindingEvent.Detected);
    }

    [Fact]
    public void Same_fingerprint_and_active_finding_is_a_reconfirmation()
    {
        var engine = NewEngine();
        var first = engine.Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            Array.Empty<Finding>(), null).Finding!;

        // Second detection via integral → confidence ascends to alta, timesConfirmed increments.
        var outcome = engine.Ingest(Sample(title: "different wording"), AuditMode.Integral,
            Stamp(AuditMode.Integral), new[] { first }, null);

        outcome.Kind.Should().Be(IngestionKind.Reconfirmed);
        outcome.Finding.Should().BeSameAs(first);
        first.Confidence.Should().Be(Confidence.Alta);
        first.TimesConfirmed.Should().Be(2);
        first.History.Should().Contain(h => h.Event == FindingEvent.Confirmed);
    }

    [Fact]
    public void Same_fingerprint_but_resolved_creates_a_recurrence()
    {
        var engine = NewEngine();
        var old = engine.Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            Array.Empty<Finding>(), null).Finding!;
        old.Resolve(new ResolutionStamp(T0, ResolutionVia.Manual, AuditMode.Lotes, "abc1234", "alvaro", "fixed"));

        var outcome = engine.Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            new[] { old }, null);

        outcome.Kind.Should().Be(IngestionKind.Recurrence);
        outcome.Finding!.Id.Should().NotBe(old.Id);
        outcome.Finding.RecurrenceOf.Should().Be(old.Id);
        outcome.Finding.Status.Should().Be(FindingStatus.Activo);
        outcome.Finding.History.Should().Contain(h => h.Event == FindingEvent.Recurrence);
    }

    [Fact]
    public void Live_silence_suppresses_the_finding()
    {
        string fp = Fingerprint.Compute("errores.recursos.no-liberado", "src/Db/Pool.cs", "Pool.Acquire", "Conn leaked");
        var silence = new Silence { Fingerprint = fp, By = "maria", Utc = T0, Reason = SilenceReason.DeudaAceptada };

        var outcome = NewEngine().Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            Array.Empty<Finding>(), liveSilence: silence);

        outcome.Kind.Should().Be(IngestionKind.SuppressedBySilence);
        outcome.Finding.Should().BeNull();
        outcome.SuppressingSilence.Should().BeSameAs(silence);
    }

    [Fact]
    public void Expired_silence_lets_a_silenced_finding_reappear()
    {
        var engine = NewEngine();
        var finding = engine.Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            Array.Empty<Finding>(), null).Finding!;
        finding.MarkSilenced(T0, "maria", "silenced");

        // Silence has expired → liveSilence is null → re-detection reactivates the finding.
        var outcome = engine.Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            new[] { finding }, liveSilence: null);

        outcome.Kind.Should().Be(IngestionKind.Reconfirmed);
        outcome.Finding.Should().BeSameAs(finding);
        finding.Status.Should().Be(FindingStatus.Activo);
        finding.History.Should().Contain(h => h.Event == FindingEvent.Unsilenced);
    }

    [Fact]
    public void Fingerprint_is_returned_even_when_suppressed()
    {
        string fp = Fingerprint.Compute("errores.recursos.no-liberado", "src/Db/Pool.cs", "Pool.Acquire", "Conn leaked");
        var silence = new Silence { Fingerprint = fp, By = "maria", Utc = T0, Reason = SilenceReason.Otro };

        var outcome = NewEngine().Ingest(Sample(), AuditMode.Lotes, Stamp(AuditMode.Lotes),
            Array.Empty<Finding>(), silence);

        outcome.Fingerprint.Should().Be(fp);
    }
}
