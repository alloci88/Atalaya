using System.Text.Json;
using System.Text.Json.Serialization;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Storage.Json;

/// <summary>
/// Canonical JSON options for the hub (§2). camelCase properties, exact enum wire values,
/// stable (indented) output so git diffs are readable and conflicts are rare.
/// </summary>
public static class AtalayaJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    /// <summary>
    /// Serializes to the canonical hub JSON text, with LF line endings and a trailing
    /// newline so every writer produces byte-identical output regardless of OS — that is
    /// what keeps ULID-named files from ever conflicting at the text level.
    /// </summary>
    public static string Serialize<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, Options);
        return json.Replace("\r\n", "\n") + "\n";
    }

    /// <summary>Deserializes from hub JSON text; throws <see cref="JsonException"/> on malformed input.</summary>
    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)
           ?? throw new JsonException($"Deserialized null for {typeof(T).Name}.");

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = true,
        };

        o.Converters.Add(new UlidJsonConverter());
        o.Converters.Add(new UtcDateTimeOffsetConverter());

        o.Converters.Add(new EnumJsonConverter<Pillar>(
            (Pillar.Optimizacion, "optimizacion"),
            (Pillar.Mejoras, "mejoras"),
            (Pillar.Errores, "errores")));

        o.Converters.Add(new EnumJsonConverter<Severity>(
            (Severity.Critica, "critica"),
            (Severity.Alta, "alta"),
            (Severity.Media, "media"),
            (Severity.Baja, "baja")));

        o.Converters.Add(new EnumJsonConverter<Confidence>(
            (Confidence.Alta, "alta"),
            (Confidence.Media, "media"),
            (Confidence.Baja, "baja")));

        o.Converters.Add(new EnumJsonConverter<FindingStatus>(
            (FindingStatus.Activo, "activo"),
            (FindingStatus.Resuelto, "resuelto"),
            (FindingStatus.Silenciado, "silenciado")));

        o.Converters.Add(new EnumJsonConverter<FindingTag>(
            (FindingTag.Checklist, "checklist"),
            (FindingTag.Criterio, "criterio")));

        o.Converters.Add(new EnumJsonConverter<AuditMode>(
            (AuditMode.Integral, "integral"),
            (AuditMode.Lotes, "lotes"),
            (AuditMode.Superficial, "superficial"),
            (AuditMode.Verify, "verify"),
            (AuditMode.Cierre, "cierre"),
            (AuditMode.Reset, "reset"),
            (AuditMode.Fix, "fix")));

        o.Converters.Add(new EnumJsonConverter<SessionTrigger>(
            (SessionTrigger.Manual, "manual"),
            (SessionTrigger.Deriva, "deriva")));

        o.Converters.Add(new EnumJsonConverter<UnitState>(
            (UnitState.Pendiente, "pendiente"),
            (UnitState.Auditada, "auditada"),
            (UnitState.Grande, "grande")));

        o.Converters.Add(new EnumJsonConverter<SilenceReason>(
            (SilenceReason.FalsoPositivo, "falso-positivo"),
            (SilenceReason.DeudaAceptada, "deuda-aceptada"),
            (SilenceReason.DecisionArquitectonica, "decision-arquitectonica"),
            (SilenceReason.Otro, "otro")));

        o.Converters.Add(new EnumJsonConverter<Verdict>(
            (Verdict.Confirmado, "confirmado"),
            (Verdict.Resuelto, "resuelto"),
            (Verdict.NoVerificable, "no-verificable")));

        o.Converters.Add(new EnumJsonConverter<ReconcileVerdict>(
            (ReconcileVerdict.Presente, "presente"),
            (ReconcileVerdict.Arreglado, "arreglado"),
            (ReconcileVerdict.NoVerificable, "no-verificable"),
            (ReconcileVerdict.NoEsDefecto, "no-es-defecto")));

        o.Converters.Add(new EnumJsonConverter<ResolutionVia>(
            (ResolutionVia.Implicita, "implicita"),
            (ResolutionVia.Auditor, "auditor"),
            (ResolutionVia.Verify, "verify"),
            (ResolutionVia.Manual, "manual"),
            (ResolutionVia.CodigoEliminado, "codigo-eliminado"),
            (ResolutionVia.Medida, "medida")));

        // History event names are camelCase in the schema (§2).
        o.Converters.Add(new EnumJsonConverter<FindingEvent>(
            (FindingEvent.Detected, "detected"),
            (FindingEvent.Confirmed, "confirmed"),
            (FindingEvent.Resolved, "resolved"),
            (FindingEvent.Reopened, "reopened"),
            (FindingEvent.Assigned, "assigned"),
            (FindingEvent.SeverityChanged, "severityChanged"),
            (FindingEvent.Silenced, "silenced"),
            (FindingEvent.Unsilenced, "unsilenced"),
            (FindingEvent.Commented, "commented"),
            (FindingEvent.Recurrence, "recurrence"),
            (FindingEvent.FixProposed, "fixProposed"),
            (FindingEvent.Disputed, "disputed"),
            (FindingEvent.DisputeCleared, "disputeCleared"),
            (FindingEvent.NotLocated, "notLocated"),
            (FindingEvent.Reanchored, "reanchored"),
            (FindingEvent.Inconclusive, "inconclusive")));

        o.Converters.Add(new EnumJsonConverter<TechStack>(
            (TechStack.Unknown, "unknown"),
            (TechStack.DotNet, "dotnet"),
            (TechStack.JavaScript, "javascript"),
            (TechStack.TypeScript, "typescript"),
            (TechStack.Python, "python"),
            (TechStack.Go, "go"),
            (TechStack.Java, "java"),
            (TechStack.Rust, "rust"),
            (TechStack.CCpp, "ccpp")));

        return o;
    }
}
