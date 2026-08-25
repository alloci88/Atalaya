using System.Text.Json;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage.Json;

namespace Atalaya.Storage;

/// <summary>
/// Migración one-shot de silencios: de la clave por <c>fingerprint</c> (pre-F4) a la clave por
/// ULID del hallazgo.
/// <para>
/// Los silencios v4/F3 se guardaban en <c>silences/{hashHex}.json</c> con un campo
/// <c>fingerprint</c> y una lista <c>findingUlids</c> de procedencia. F4 elimina el fingerprint
/// como clave de nada, así que el silencio pasa a nombrarse <c>silences/{ulid}.json</c> con el
/// campo <c>findingUlid</c>. El ULID de destino es el que el propio silencio ya referenciaba.
/// </para>
/// <para>
/// Idempotente: un fichero ya nombrado por un ULID válido se deja intacto. Un silencio sin
/// <c>findingUlids</c> no es migrable (no hay a qué hallazgo apuntarlo) y se deja donde está,
/// reportado en el resultado — nunca se borra nada en silencio.
/// </para>
/// </summary>
public static class SilenceMigration
{
    /// <param name="Migrated">Ficheros reescritos con el nombre nuevo.</param>
    /// <param name="Skipped">Ficheros no migrables, con el motivo.</param>
    public sealed record Result(IReadOnlyList<string> Migrated, IReadOnlyList<string> Skipped);

    /// <summary>Migra los silencios de una app. No toca ningún otro fichero del hub.</summary>
    public static Result MigrateApp(HubPaths paths, string slug)
    {
        var migrated = new List<string>();
        var skipped = new List<string>();

        string dir = paths.SilencesDir(slug);
        if (!Directory.Exists(dir))
        {
            return new Result(migrated, skipped);
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (Ulid.TryParse(name, out _))
            {
                continue; // ya migrado
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                if (!TryFindingUlid(root, out Ulid target))
                {
                    skipped.Add($"{name}: sin findingUlids — no hay hallazgo al que anclarlo.");
                    continue;
                }

                var silence = new Silence
                {
                    FindingUlid = target,
                    By = Text(root, "by") ?? "desconocido",
                    Notes = Text(root, "notes"),
                    Reason = ReadReason(root),
                    Utc = Date(root, "utc") ?? DateTimeOffset.UnixEpoch,
                    ExpiresUtc = Date(root, "expiresUtc"),
                };

                string destination = paths.SilenceFile(slug, target.ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, AtalayaJson.Serialize(silence));
                File.Delete(file);
                migrated.Add($"{name} → {target}");
            }
            catch (Exception ex)
            {
                skipped.Add($"{name}: ilegible ({ex.Message}).");
            }
        }

        return new Result(migrated, skipped);
    }

    private static bool TryFindingUlid(JsonElement root, out Ulid ulid)
    {
        ulid = default;
        if (root.TryGetProperty("findingUlid", out JsonElement single)
            && single.ValueKind == JsonValueKind.String
            && Ulid.TryParse(single.GetString(), out ulid))
        {
            return true;
        }

        if (!root.TryGetProperty("findingUlids", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Ulid.TryParse(item.GetString(), out ulid))
            {
                return true;
            }
        }

        return false;
    }

    private static SilenceReason ReadReason(JsonElement root)
        => Text(root, "reason") switch
        {
            "falso-positivo" => SilenceReason.FalsoPositivo,
            "deuda-aceptada" => SilenceReason.DeudaAceptada,
            "decision-arquitectonica" => SilenceReason.DecisionArquitectonica,
            _ => SilenceReason.Otro,
        };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static DateTimeOffset? Date(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(e.GetString(), out DateTimeOffset d) ? d : null;
}
