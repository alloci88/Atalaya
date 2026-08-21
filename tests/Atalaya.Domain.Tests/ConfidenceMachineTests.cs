using Atalaya.Domain;
using Atalaya.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

public class ConfidenceMachineTests
{
    // --- New findings (§0): integral→alta, lotes→media, superficial→baja. ---

    [Theory]
    [InlineData(AuditMode.Integral, Confidence.Alta)]
    [InlineData(AuditMode.Lotes, Confidence.Media)]
    [InlineData(AuditMode.Superficial, Confidence.Baja)]
    public void ForNew_assigns_birth_confidence(AuditMode mode, Confidence expected)
    {
        ConfidenceMachine.ForNew(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData(AuditMode.Verify)]
    [InlineData(AuditMode.Cierre)]
    [InlineData(AuditMode.Reset)]
    [InlineData(AuditMode.Fix)]
    public void ForNew_rejects_modes_that_never_create_findings(AuditMode mode)
    {
        var act = () => ConfidenceMachine.ForNew(mode);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- Reconfirmation matrix (§0). This is the heart; cover every cell. ---

    [Theory]
    // verify never changes confidence
    [InlineData(Confidence.Alta, AuditMode.Verify, false, Confidence.Alta)]
    [InlineData(Confidence.Media, AuditMode.Verify, false, Confidence.Media)]
    [InlineData(Confidence.Baja, AuditMode.Verify, false, Confidence.Baja)]
    // integral always ascends to alta
    [InlineData(Confidence.Baja, AuditMode.Integral, false, Confidence.Alta)]
    [InlineData(Confidence.Media, AuditMode.Integral, false, Confidence.Alta)]
    [InlineData(Confidence.Alta, AuditMode.Integral, false, Confidence.Alta)]
    // lotes single-unit: baja→media, otherwise unchanged
    [InlineData(Confidence.Baja, AuditMode.Lotes, false, Confidence.Media)]
    [InlineData(Confidence.Media, AuditMode.Lotes, false, Confidence.Media)]
    [InlineData(Confidence.Alta, AuditMode.Lotes, false, Confidence.Alta)]
    // superficial never ascends
    [InlineData(Confidence.Baja, AuditMode.Superficial, false, Confidence.Baja)]
    [InlineData(Confidence.Media, AuditMode.Superficial, false, Confidence.Media)]
    [InlineData(Confidence.Alta, AuditMode.Superficial, false, Confidence.Alta)]
    // cycle close of lotes ascends to alta regardless of mode carrier
    [InlineData(Confidence.Media, AuditMode.Lotes, true, Confidence.Alta)]
    [InlineData(Confidence.Baja, AuditMode.Lotes, true, Confidence.Alta)]
    [InlineData(Confidence.Media, AuditMode.Cierre, false, Confidence.Alta)]
    public void OnReconfirm_matrix(Confidence current, AuditMode mode, bool cycleClose, Confidence expected)
    {
        ConfidenceMachine.OnReconfirm(current, mode, cycleClose).Should().Be(expected);
    }

    [Fact]
    public void Verify_wins_even_when_cycleClose_is_true()
    {
        // Defensive: verify should never change confidence, even if a caller passes cycleClose.
        ConfidenceMachine.OnReconfirm(Confidence.Baja, AuditMode.Verify, cycleClose: true)
            .Should().Be(Confidence.Baja);
    }
}
