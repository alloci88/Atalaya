using System.Text.Json;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage.Json;

namespace Atalaya.Storage;

/// <summary>
/// Migración one-shot de F5.10 a F5.12: las <b>exclusiones por regla</b> pasan a ser
/// <b>patrones silenciados</b>.
/// <para>
/// La exclusión por regla se retiró porque convertía el silenciado en mantenimiento de taxonomía
/// (ver DECISIONS, F5.12). Lo que quedara escrito en <c>apps/{slug}/rule-exclusions/</c> no se
/// tira: cada exclusión se convierte en un patrón cuyo ejemplar es la <b>descripción de la regla</b>
/// del catálogo —lo que la regla buscaba, en una frase—, que es exactamente el tipo de problema
/// que su autor quiso callar. Si la regla ya no está en el catálogo, el ejemplar es su
/// <c>ruleId</c>: menos legible, pero nunca vacío y siempre editable desde la gestión.
/// </para>
/// <para>
/// Idempotente y sin pérdida: solo escribe si el directorio legado existe y tiene ficheros, y
/// borra cada origen únicamente después de haber escrito su destino. Un fichero ilegible se deja
/// donde está y se reporta — nunca se borra nada en silencio.
/// </para>
/// </summary>
public static class RuleExclusionMigration
{
    /// <param name="Migrated">Exclusiones convertidas en patrón: <c>ruleId → P-n</c>.</param>
    /// <param name="Skipped">Ficheros no migrables, con el motivo.</param>
    public sealed record Result(IReadOnlyList<string> Migrated, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Traduce el <c>ruleId</c> a la frase que describe el tipo de problema. La firma toma un
    /// diccionario en vez de leer el catálogo porque el catálogo vive en <c>Atalaya.Agents</c>
    /// —desde PROV-2; antes, en el proyecto de una casa— y <c>Storage</c> no lo referencia: es la
    /// capa de disco, y lo que sabe de reglas se lo dice quien la llama.
    /// </summary>
    public delegate string? DescribeRule(string ruleId);

    /// <summary>Migra las exclusiones de una app. No toca ningún otro fichero del hub.</summary>
    public static Result MigrateApp(
        HubPaths paths, string slug, IUlidFactory ulids, DateTimeOffset now, DescribeRule? describe = null)
    {
        var migrated = new List<string>();
        var skipped = new List<string>();

        string dir = paths.LegacyRuleExclusionsDir(slug);
        if (!Directory.Exists(dir))
        {
            return new Result(migrated, skipped);
        }

        // Los que ya existen mandan sobre el id corto: migrar dos veces no puede producir dos P-1.
        var existing = ReadExistingPatterns(paths, slug);

        foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                string ruleId = Text(root, "ruleId") ?? Path.GetFileNameWithoutExtension(file);
                string exemplar = Blank(describe?.Invoke(ruleId)) ?? ruleId;

                var pattern = new PatternSilence
                {
                    Id = ulids.NewUlid(),
                    ShortId = PatternShortId.Next(existing),
                    Exemplar = exemplar,
                    SourceFindingUlid = null,
                    Reason = ReadReason(root),
                    Notes = Note(Text(root, "notes"), ruleId),
                    By = Text(root, "by") ?? "desconocido",
                    Utc = Date(root, "utc") ?? now,
                    ExpiresUtc = Date(root, "expiresUtc"),
                };

                string destination = paths.PatternSilenceFile(slug, pattern.Id.ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, AtalayaJson.Serialize(pattern));
                File.Delete(file);
                existing.Add(pattern);
                migrated.Add($"{ruleId} → {pattern.ShortId}");
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(file)}: ilegible ({ex.Message}).");
            }
        }

        // El directorio vacío se retira: dejarlo sugiere que la exclusión por regla sigue siendo
        // una superficie del producto, y ya no lo es.
        if (skipped.Count == 0 && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try
            {
                Directory.Delete(dir);
            }
            catch (IOException)
            {
                // Que quede la carpeta vacía no rompe nada; no merece hacer fallar la migración.
            }
        }

        return new Result(migrated, skipped);
    }

    /// <summary>
    /// La procedencia queda escrita en las notas: quien lea el patrón dentro de un año tiene que
    /// poder saber que no lo escribió nadie, que salió de una exclusión de regla que ya no existe.
    /// </summary>
    private static string Note(string? original, string ruleId)
    {
        string origin = $"Migrado de la exclusión de regla {ruleId} (F5.10 → F5.12).";
        return string.IsNullOrWhiteSpace(original) ? origin : $"{original!.Trim()} · {origin}";
    }

    private static List<PatternSilence> ReadExistingPatterns(HubPaths paths, string slug)
    {
        var result = new List<PatternSilence>();
        string dir = paths.PatternSilencesDir(slug);
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                result.Add(AtalayaJson.Deserialize<PatternSilence>(File.ReadAllText(file)));
            }
            catch (Exception)
            {
                // Un patrón ilegible no puede impedir la migración; lo peor que pasa es que su id
                // corto se reutilice, y el id corto no es una identidad histórica.
            }
        }

        return result;
    }

    private static SilenceReason ReadReason(JsonElement root)
        => Text(root, "reason") switch
        {
            "falso-positivo" => SilenceReason.FalsoPositivo,
            "deuda-aceptada" => SilenceReason.DeudaAceptada,
            "decision-arquitectonica" => SilenceReason.DecisionArquitectonica,
            _ => SilenceReason.Otro,
        };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static DateTimeOffset? Date(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(e.GetString(), out DateTimeOffset d) ? d : null;
}
