using LibGit2Sharp;

namespace Atalaya.Storage.Tests;

/// <summary>
/// A throwaway temp directory tree with a bare "remote" and helpers to create per-user
/// clones — the two-clone harness §11 asks for (a local <c>--bare</c> remote).
/// </summary>
public sealed class TempRepo : IDisposable
{
    private readonly string _base;

    public TempRepo()
    {
        _base = Path.Combine(Path.GetTempPath(), "atalaya-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
        BareRemotePath = Path.Combine(_base, "remote.git");
        Repository.Init(BareRemotePath, isBare: true);
    }

    /// <summary>Path to the bare remote, usable directly as a clone URL.</summary>
    public string BareRemotePath { get; }

    /// <summary>Creates a fresh clone working directory path (not yet cloned).</summary>
    public string NewClonePath(string name) => Path.Combine(_base, name);

    public void Dispose()
    {
        try
        {
            // git pack files are often read-only; clear the attribute before deleting.
            foreach (string file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a locked git handle should never fail a test.
        }
    }
}
