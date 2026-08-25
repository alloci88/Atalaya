using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.ImportV4;

/// <summary>
/// Tolerant importer of the v4 markdown format (§9). The markdown was written by agents, so parsing
/// is forgiving: it reports what it cannot import in <see cref="ImportResult.Log"/> and never fails
/// the whole import for one corrupt entry.
/// </summary>
public sealed class V4Importer
{
    private static readonly Regex FindingHeading =
        new(@"^#{1,4}\s+(?<id>[A-Za-z]{2,4}-\d+)\s*(\[(?<sev>[^\]]+)\])?\s*(?<title>.*)$", RegexOptions.Compiled);
    private static readonly Regex SilenceHeading =
        new(@"^#{1,4}\s+(?<id>[A-Za-z]{2,4}-\d+)\s*(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex Bullet =
        new(@"^\s*[-*]\s*(?<key>[A-Za-z áéíóúÁÉÍÓÚñÑ/]+)\s*[:=]\s*(?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex LotesLine =
        new(@"^\s*[-*]\s*\[(?<mark>[ xX~gG])\]\s*(?<path>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex CycleHeading = new(@"ciclo\s+(?<n>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HistoLine =
        new(@"^\s*[-*]\s*(?<date>\d{4}-\d{2}-\d{2})\S*\s+(?<mode>\w+)\s+(?<by>\S+)", RegexOptions.Compiled);

    private readonly IUlidFactory _ulids;

    public V4Importer(IUlidFactory? ulids = null) => _ulids = ulids ?? new UlidFactory(SystemClock.Instance);

    public ImportResult Import(string codeAuditDir)
    {
        var result = new ImportResult();
        if (!Directory.Exists(codeAuditDir))
        {
            result.Note($"Carpeta no encontrada: {codeAuditDir}");
            return result;
        }

        var byDisplayId = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
        ParseBaseline(codeAuditDir, result, byDisplayId);
        ParseSilences(codeAuditDir, result, byDisplayId);
        ParseLotes(codeAuditDir, result);
        ParseHistorico(codeAuditDir, result);
        CopyReports(codeAuditDir, result);
        return result;
    }

    private void ParseBaseline(string dir, ImportResult result, Dictionary<string, Finding> byDisplayId)
    {
        string? path = FindFile(dir, "BASELINE.md");
        if (path is null)
        {
            result.Note("Sin BASELINE.md.");
            return;
        }

        foreach ((string heading, Dictionary<string, string> fields, string _) in Blocks(File.ReadAllText(path), FindingHeading))
        {
            try
            {
                Match m = FindingHeading.Match(heading);
                string displayId = m.Groups["id"].Value;
                string title = m.Groups["title"].Value.Trim();
                Pillar pillar = ParsePillar(Get(fields, "pilar", "pillar") ?? displayId);
                Severity severity = ParseSeverity(m.Groups["sev"].Value, Get(fields, "severidad", "severity"));
                Confidence confidence = ParseConfidence(Get(fields, "confianza", "confidence"));
                var locations = ParseLocations(Get(fields, "ubicacion", "ubicaciones", "location", "locations"));
                if (locations.Count == 0)
                {
                    locations.Add(new Location("desconocido", 0));
                }

                string ruleId = Get(fields, "regla", "ruleid", "rule") ?? $"criterio.importado.{pillar.ToString().ToLowerInvariant()}";
                var first = ParseStamp(Get(fields, "primera", "first", "detectado"));
                var last = ParseStamp(Get(fields, "ultima", "last", "confirmado")) ?? first;

                var finding = new Finding
                {
                    Id = _ulids.NewUlid(),
                    DisplayId = displayId,
                    RuleId = ruleId,
                    Pillar = pillar,
                    Tag = FindingTag.Criterio,
                    Severity = severity,
                    Confidence = confidence,
                    Status = FindingStatus.Activo,
                    Title = title.Length == 0 ? displayId : title,
                    Description = Get(fields, "descripcion", "description") ?? "",
                    Impact = Get(fields, "impacto", "impact") ?? "",
                    Recommendation = Get(fields, "recomendacion", "recommendation") ?? "",
                    Locations = locations,
                    Origin = AuditMode.Integral,
                    FirstDetected = first ?? new DetectionStamp(DateTimeOffset.UnixEpoch, AuditMode.Integral, "import", "import"),
                    LastConfirmed = last ?? new DetectionStamp(DateTimeOffset.UnixEpoch, AuditMode.Integral, "import", "import"),
                    TimesConfirmed = ParseInt(Get(fields, "veces", "times", "timesconfirmed")) ?? 1,
                };
                finding.History.Add(new HistoryEntry(finding.FirstDetected.Utc, FindingEvent.Detected, "import", "importado de v4"));

                result.Findings.Add(finding);
                byDisplayId[displayId] = finding;
            }
            catch (Exception ex)
            {
                result.Note($"Hallazgo no importable ('{heading.Trim()}'): {ex.Message}");
            }
        }
    }

    private void ParseSilences(string dir, ImportResult result, Dictionary<string, Finding> byDisplayId)
    {
        string? path = FindFile(dir, "SILENCIADOS.md", "SILENCIOS.md");
        if (path is null)
        {
            return;
        }

        foreach ((string heading, Dictionary<string, string> fields, string _) in Blocks(File.ReadAllText(path), SilenceHeading))
        {
            try
            {
                string displayId = SilenceHeading.Match(heading).Groups["id"].Value;
                if (!byDisplayId.TryGetValue(displayId, out Finding? finding))
                {
                    result.Note($"Silencio sin hallazgo correspondiente: {displayId}.");
                    continue;
                }

                var silence = new Silence
                {
                    FindingUlid = finding.Id,
                    Reason = ParseReason(Get(fields, "motivo", "reason")),
                    Notes = Get(fields, "notas", "notes"),
                    By = Get(fields, "por", "by", "autor") ?? "import",
                    Utc = ParseDate(Get(fields, "fecha", "date")) ?? DateTimeOffset.UnixEpoch,
                    ExpiresUtc = ParseDate(Get(fields, "caduca", "expires", "expira")),
                };

                finding.MarkSilenced(silence.Utc, silence.By, "silenciado (import v4)");
                result.Silences.Add(silence);
            }
            catch (Exception ex)
            {
                result.Note($"Silencio no importable ('{heading.Trim()}'): {ex.Message}");
            }
        }
    }

    private void ParseLotes(string dir, ImportResult result)
    {
        string? path = FindFile(dir, "LOTES.md");
        if (path is null)
        {
            return;
        }

        string text = File.ReadAllText(path);
        int cycle = 1;
        Match cm = CycleHeading.Match(text);
        if (cm.Success && int.TryParse(cm.Groups["n"].Value, out int n))
        {
            cycle = n;
        }

        var inv = new InventoryCycle { CycleN = cycle };
        foreach (string line in text.Split('\n'))
        {
            Match m = LotesLine.Match(line);
            if (!m.Success)
            {
                continue;
            }

            string unitPath = m.Groups["path"].Value.Trim();
            UnitState state = m.Groups["mark"].Value.ToLowerInvariant() switch
            {
                "x" => UnitState.Auditada,
                "g" => UnitState.Grande,
                _ => UnitState.Pendiente, // "[ ]" and "[~]" (claimed → pending on import)
            };
            inv.Units.Add(new InventoryUnit
            {
                Path = unitPath,
                Module = ModuleOf(unitPath),
                Loc = 0,
                State = state,
            });
        }

        if (inv.Units.Count > 0)
        {
            result.Inventory = inv;
        }
    }

    private void ParseHistorico(string dir, ImportResult result)
    {
        string? path = FindFile(dir, "HISTORICO.md", "HISTORIAL.md");
        if (path is null)
        {
            return;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            Match m = HistoLine.Match(line);
            if (!m.Success)
            {
                continue;
            }

            try
            {
                result.Sessions.Add(new AuditSession
                {
                    Id = _ulids.NewUlid(),
                    AppSlug = "importado",
                    Mode = ParseModeLoose(m.Groups["mode"].Value),
                    By = m.Groups["by"].Value,
                    Machine = "import",
                    StartedUtc = ParseDate(m.Groups["date"].Value) ?? DateTimeOffset.UnixEpoch,
                    EndedUtc = ParseDate(m.Groups["date"].Value),
                    CycleN = 1,
                });
            }
            catch (Exception ex)
            {
                result.Note($"Sesión histórica no importable ('{line.Trim()}'): {ex.Message}");
            }
        }
    }

    private static void CopyReports(string dir, ImportResult result)
    {
        string reportsDir = Path.Combine(dir, "reports");
        if (!Directory.Exists(reportsDir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(reportsDir, "*.md"))
        {
            result.Reports.Add(new ImportedReport(Path.GetFileName(file), File.ReadAllText(file)));
        }
    }

    // --- block/field parsing ---

    private static IEnumerable<(string Heading, Dictionary<string, string> Fields, string Body)> Blocks(string text, Regex headingRegex)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        string? heading = null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = new StringBuilder();

        foreach (string line in lines)
        {
            if (headingRegex.IsMatch(line) && line.TrimStart().StartsWith('#'))
            {
                if (heading is not null)
                {
                    yield return (heading, fields, body.ToString());
                }

                heading = line;
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                body = new StringBuilder();
                continue;
            }

            Match b = Bullet.Match(line);
            if (b.Success)
            {
                fields[Normalize(b.Groups["key"].Value)] = b.Groups["value"].Value.Trim();
            }
            else
            {
                body.AppendLine(line);
            }
        }

        if (heading is not null)
        {
            yield return (heading, fields, body.ToString());
        }
    }

    private static string? Get(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(Normalize(key), out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static List<Location> ParseLocations(string? raw)
    {
        var result = new List<Location>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (string part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            int colon = p.LastIndexOf(':');
            if (colon > 0 && int.TryParse(p[(colon + 1)..].Trim(), out int line))
            {
                result.Add(new Location(p[..colon].Trim(), line));
            }
            else
            {
                result.Add(new Location(p, 0));
            }
        }

        return result;
    }

    private static Pillar ParsePillar(string s)
    {
        s = Normalize(s);
        if (s.StartsWith("opt") || s.Contains("optimiz")) return Pillar.Optimizacion;
        if (s.StartsWith("mej") || s.Contains("mejora")) return Pillar.Mejoras;
        return Pillar.Errores; // BUG- and anything else
    }

    private static Severity ParseSeverity(params string?[] candidates)
    {
        foreach (string? c in candidates)
        {
            switch (Normalize(c ?? ""))
            {
                case "critica": return Severity.Critica;
                case "alta": return Severity.Alta;
                case "media": return Severity.Media;
                case "baja": return Severity.Baja;
            }
        }

        return Severity.Media;
    }

    private static Confidence ParseConfidence(string? s) => Normalize(s ?? "") switch
    {
        "alta" => Confidence.Alta,
        "baja" => Confidence.Baja,
        _ => Confidence.Media,
    };

    private static SilenceReason ParseReason(string? s) => Normalize(s ?? "") switch
    {
        var x when x.Contains("falso") => SilenceReason.FalsoPositivo,
        var x when x.Contains("deuda") => SilenceReason.DeudaAceptada,
        var x when x.Contains("arquitect") => SilenceReason.DecisionArquitectonica,
        _ => SilenceReason.Otro,
    };

    private static AuditMode ParseModeLoose(string s) => Normalize(s) switch
    {
        "integral" => AuditMode.Integral,
        "superficial" => AuditMode.Superficial,
        "verify" => AuditMode.Verify,
        "cierre" => AuditMode.Cierre,
        "reset" => AuditMode.Reset,
        _ => AuditMode.Lotes,
    };

    private static DetectionStamp? ParseStamp(string? s)
    {
        DateTimeOffset? d = ParseDate(s);
        return d is null ? null : new DetectionStamp(d.Value, AuditMode.Integral, "import", "import");
    }

    private static DateTimeOffset? ParseDate(string? s)
        => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset d) ? d : null;

    private static int? ParseInt(string? s) => int.TryParse(s, out int i) ? i : null;

    private static string ModuleOf(string path)
    {
        string norm = path.Replace('\\', '/');
        int slash = norm.LastIndexOf('/');
        if (slash <= 0)
        {
            return "(root)";
        }

        string dir = norm[..slash];
        int prev = dir.LastIndexOf('/');
        return prev < 0 ? dir : dir[(prev + 1)..];
    }

    private static string? FindFile(string dir, params string[] names)
    {
        foreach (string name in names)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string Normalize(string s)
    {
        string lower = s.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (char c in lower.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).Trim();
    }
}
