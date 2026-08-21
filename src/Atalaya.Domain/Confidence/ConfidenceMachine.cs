namespace Atalaya.Domain.Rules;

/// <summary>
/// The confidence state machine (§0), implemented literally. Kept as a pure static
/// function of (current confidence, mode, cycle-close) so it is exhaustively testable.
/// </summary>
public static class ConfidenceMachine
{
    /// <summary>Confidence a brand-new finding is born with, by the mode that detected it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If <paramref name="mode"/> is a mode that never mints new findings
    /// (verify/cierre/reset/fix).
    /// </exception>
    public static Confidence ForNew(AuditMode mode) => mode switch
    {
        AuditMode.Integral => Confidence.Alta,
        AuditMode.Lotes => Confidence.Media,
        AuditMode.Superficial => Confidence.Baja,
        _ => throw new ArgumentOutOfRangeException(nameof(mode),
            $"Mode '{mode}' never creates new findings."),
    };

    /// <summary>
    /// Confidence after a reconfirmation. Rules (§0):
    /// <list type="bullet">
    ///   <item>verify → never changes;</item>
    ///   <item>cycle close of lotes → ascends to alta;</item>
    ///   <item>integral → ascends to alta;</item>
    ///   <item>lotes (single unit) → baja ascends to media, otherwise unchanged;</item>
    ///   <item>superficial → does not ascend.</item>
    /// </list>
    /// </summary>
    public static Confidence OnReconfirm(Confidence current, AuditMode mode, bool cycleClose)
    {
        // Verify refreshes lastConfirmed but NEVER touches confidence.
        if (mode == AuditMode.Verify)
        {
            return current;
        }

        // Cycle close of a lotes cycle promotes to alta.
        if (cycleClose || mode == AuditMode.Cierre)
        {
            return Confidence.Alta;
        }

        return mode switch
        {
            AuditMode.Integral => Confidence.Alta,
            AuditMode.Lotes => current == Confidence.Baja ? Confidence.Media : current,
            AuditMode.Superficial => current,
            _ => current,
        };
    }
}
