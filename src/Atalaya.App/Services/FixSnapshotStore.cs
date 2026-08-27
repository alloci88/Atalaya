using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atalaya.App.Services;

/// <summary>Un fichero tocado por una sesión de arreglo, con su copia de antes.</summary>
/// <param name="RelativePath">Ruta relativa al clon, con barras normales.</param>
/// <param name="BackupFile">Nombre del fichero de respaldo dentro de la carpeta de la sesión.</param>
/// <param name="ExistedBefore">
/// Si el fichero existía antes. Cuando no, descartar significa BORRARLO: restaurar un fichero
/// nuevo a «su contenido anterior» sería dejar un fichero vacío donde no había nada.
/// </param>
public sealed record FixSnapshotEntry(string RelativePath, string BackupFile, bool ExistedBefore);

/// <summary>
/// El registro completo de lo que una sesión de arreglo tocó (F6.9 §3). Vive en
/// <c>%LOCALAPPDATA%</c>, <b>nunca dentro del clon</b>: si viviera en el clon, el propio registro
/// de seguridad sería un cambio sin commitear más — y el descarte tendría que descartarse a sí
/// mismo.
/// </summary>
public sealed class FixSnapshotSet
{
    public required string SessionId { get; set; }

    public required string Slug { get; set; }

    public required string CloneRoot { get; set; }

    /// <summary>El hallazgo que se estaba arreglando, para poder nombrarlo al recuperar.</summary>
    public string? FindingAlias { get; set; }

    public string? FindingTitle { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public List<FixSnapshotEntry> Entries { get; set; } = new();

    /// <summary>Los cambios ya se descartaron o el usuario los dio por buenos.</summary>
    public bool Closed { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> Files => Entries.Select(e => e.RelativePath).ToList();
}

/// <summary>Qué pasó al descartar.</summary>
/// <param name="Restored">Ficheros devueltos a su contenido anterior.</param>
/// <param name="Deleted">Ficheros que el agente creó y que se han borrado.</param>
/// <param name="Failed">Los que no se pudieron tocar, con el motivo.</param>
public sealed record FixRestoreReport(int Restored, int Deleted, IReadOnlyList<string> Failed)
{
    public bool Ok => Failed.Count == 0;

    public string Message => Ok
        ? $"Cambios descartados: {Restored} fichero(s) restaurado(s)"
          + (Deleted > 0 ? $" y {Deleted} creado(s) por el agente borrado(s)" : "") + "."
        : $"Descarte incompleto: {Restored} restaurado(s), {Deleted} borrado(s), "
          + $"{Failed.Count} sin poder tocar ({string.Join("; ", Failed)}).";
}

/// <summary>
/// Guarda y restaura el contenido previo de cada fichero que una sesión de arreglo toca.
/// <para>
/// <b>Es la única razón por la que el arreglo puede trabajar sin rama</b> (F6.9 §1). La seguridad
/// no viene de una rama de git: viene de que el árbol estuviera limpio al empezar, de que quede
/// registrado byte a byte lo que había antes de cada fichero, y de que descartar sea un botón.
/// </para>
/// <para>
/// La copia se hace <b>una sola vez por fichero</b>, en la PRIMERA edición: la segunda edición del
/// mismo fichero ya no es «lo que había antes de la sesión». Y se copia el fichero entero en
/// binario, no su texto: el descarte tiene que devolver el fichero exacto —codificación, BOM y
/// finales de línea incluidos—, no uno equivalente.
/// </para>
/// </summary>
public sealed class FixSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    public FixSnapshotStore(AppPaths paths) => _root = paths.Fixes;

    /// <summary>La carpeta de una sesión de arreglo.</summary>
    public string FolderFor(string sessionId) => Path.Combine(_root, sessionId);

    public FixSnapshotSet Begin(
        string sessionId, string slug, string cloneRoot, string? alias, string? title)
    {
        var set = new FixSnapshotSet
        {
            SessionId = sessionId,
            Slug = slug,
            CloneRoot = Path.GetFullPath(cloneRoot),
            FindingAlias = alias,
            FindingTitle = title,
            StartedUtc = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(Path.Combine(FolderFor(sessionId), "files"));
        Save(set);
        return set;
    }

    /// <summary>
    /// Copia el estado actual de un fichero, si no se había copiado ya. Devuelve el contenido
    /// previo como texto (o <c>null</c> si el fichero no existía), que es lo que el diff necesita.
    /// </summary>
    public string? Capture(FixSnapshotSet set, string relativePath)
    {
        string relative = Normalize(relativePath);
        string full = Path.Combine(set.CloneRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        bool exists = File.Exists(full);

        FixSnapshotEntry? already = set.Entries.FirstOrDefault(
            e => string.Equals(e.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
        if (already is not null)
        {
            // Ya estaba registrado: lo de «antes» sigue siendo el respaldo de la primera edición.
            return already.ExistedBefore ? SafeReadText(BackupPath(set, already)) : null;
        }

        string backupName = $"{set.Entries.Count:D3}-{Path.GetFileName(relative)}.bak";
        var entry = new FixSnapshotEntry(relative, backupName, exists);
        if (exists)
        {
            Directory.CreateDirectory(Path.Combine(FolderFor(set.SessionId), "files"));
            File.Copy(full, BackupPath(set, entry), overwrite: true);
        }

        set.Entries.Add(entry);
        Save(set);
        return exists ? SafeReadText(full) : null;
    }

    /// <summary>Devuelve el clon exactamente a como estaba al empezar la sesión.</summary>
    public FixRestoreReport Restore(FixSnapshotSet set)
    {
        int restored = 0;
        int deleted = 0;
        var failed = new List<string>();

        foreach (FixSnapshotEntry entry in set.Entries)
        {
            string full = Path.Combine(
                set.CloneRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (entry.ExistedBefore)
                {
                    string backup = BackupPath(set, entry);
                    if (!File.Exists(backup))
                    {
                        failed.Add($"{entry.RelativePath}: falta la copia de respaldo");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.Copy(backup, full, overwrite: true);
                    restored++;
                }
                else if (File.Exists(full))
                {
                    File.Delete(full);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                failed.Add($"{entry.RelativePath}: {ex.Message}");
            }
        }

        set.Closed = failed.Count == 0;
        Save(set);
        return new FixRestoreReport(restored, deleted, failed);
    }

    public void Save(FixSnapshotSet set)
    {
        string folder = FolderFor(set.SessionId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(set, JsonOptions));
    }

    public FixSnapshotSet? Load(string sessionId)
    {
        string file = Path.Combine(FolderFor(sessionId), "manifest.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FixSnapshotSet>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Las sesiones de arreglo cuyos cambios siguen en el clon sin que nadie los haya cerrado
    /// (F6.9 §4). Es lo que permite descartar DESPUÉS —incluso tras cerrar la aplicación— en vez
    /// de perder el botón con la ventana.
    /// </summary>
    public IReadOnlyList<FixSnapshotSet> ListPending()
    {
        if (!Directory.Exists(_root))
        {
            return Array.Empty<FixSnapshotSet>();
        }

        return Directory.EnumerateDirectories(_root)
            .Select(d => Load(Path.GetFileName(d)))
            .Where(s => s is { Closed: false, Entries.Count: > 0 })
            .Select(s => s!)
            .OrderByDescending(s => s.StartedUtc)
            .ToList();
    }

    /// <summary>Cierra el registro: el usuario dio los cambios por buenos.</summary>
    public void Close(FixSnapshotSet set)
    {
        set.Closed = true;
        Save(set);
    }

    private string BackupPath(FixSnapshotSet set, FixSnapshotEntry entry)
        => Path.Combine(FolderFor(set.SessionId), "files", entry.BackupFile);

    private static string? SafeReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ruta relativa canónica: barras normales y sin barra inicial.</summary>
    public static string Normalize(string path)
        => (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
}
