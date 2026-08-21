namespace Atalaya.Inventory.Tests;

/// <summary>A throwaway on-disk repo fixture built from an in-memory file map.</summary>
public sealed class SyntheticRepo : IDisposable
{
    public SyntheticRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "atalaya-inv", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public SyntheticRepo File(string relativePath, string content)
    {
        string abs = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        System.IO.File.WriteAllText(abs, content.Replace("\r\n", "\n"));
        return this;
    }

    /// <summary>Writes a file with a given number of lines (to cross the "grande" threshold).</summary>
    public SyntheticRepo Lines(string relativePath, int lines)
        => File(relativePath, string.Join('\n', Enumerable.Range(1, lines).Select(i => $"// line {i}")));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
