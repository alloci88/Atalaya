using System.Runtime.InteropServices;

namespace Atalaya.Copilot;

/// <summary>
/// Resolves the Copilot CLI that the SDK package bundles with the application (F2.1). The
/// <c>GitHub.Copilot.SDK</c> MSBuild targets download the CLI at build time and copy it to
/// <c>runtimes/{rid}/native/copilot[.exe]</c> in the output, flowing transitively into
/// <c>Atalaya.App</c>. We pass that path explicitly to <c>RuntimeConnection.ForStdio</c> so the
/// runtime is ALWAYS the bundled binary — never a <c>copilot</c> found on PATH, and never an
/// npm install the user has to do.
/// </summary>
public static class CopilotCliLocator
{
    /// <summary>The binary name for the current OS.</summary>
    public static string BinaryName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";

    /// <summary>
    /// The portable RID the SDK targets use for the output folder name (win-x64, win-arm64,
    /// linux-x64, linux-musl-x64, osx-arm64…).
    /// </summary>
    public static string RuntimeIdentifier()
    {
        string os =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
            "linux";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        return $"{os}-{arch}";
    }

    /// <summary>
    /// The bundled CLI path, or <c>null</c> when it is not deployed next to the app (e.g. a test
    /// host that never referenced the SDK targets). Probes the exact RID first, then any other
    /// <c>runtimes/*/native</c> folder, so an odd RID mapping still finds the binary.
    /// </summary>
    public static string? ResolveBundled(string? baseDirectory = null)
    {
        string root = baseDirectory ?? AppContext.BaseDirectory;
        string runtimes = Path.Combine(root, "runtimes");
        if (!Directory.Exists(runtimes))
        {
            return null;
        }

        string exact = Path.Combine(runtimes, RuntimeIdentifier(), "native", BinaryName);
        if (File.Exists(exact))
        {
            return exact;
        }

        foreach (string rid in Directory.EnumerateDirectories(runtimes))
        {
            string candidate = Path.Combine(rid, "native", BinaryName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
