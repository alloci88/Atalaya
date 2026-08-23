using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atalaya.App.Services;

/// <summary>
/// The connected GitHub account: the user token plus the profile snapshot we derive the git
/// commit identity from. Persisted encrypted; never written to the hub.
/// </summary>
public sealed class GitHubAccount
{
    public string Token { get; set; } = string.Empty;

    public long Id { get; set; }

    public string Login { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? AvatarUrl { get; set; }

    public string? Email { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Login : Name!;

    /// <summary>Commit email: public email, else the GitHub noreply address (D2.2).</summary>
    [JsonIgnore]
    public string CommitEmail => string.IsNullOrWhiteSpace(Email)
        ? $"{Id}+{Login}@users.noreply.github.com"
        : Email!;

    public static GitHubAccount From(GitHubUser user, string token, DateTimeOffset now) => new()
    {
        Token = token,
        Id = user.Id,
        Login = user.Login,
        Name = user.Name,
        AvatarUrl = user.AvatarUrl,
        Email = user.Email,
        ConnectedAt = now,
    };
}

/// <summary>
/// Stores the account blob DPAPI-encrypted (CurrentUser) in
/// <c>%LOCALAPPDATA%/Atalaya/auth.dat</c> — the same mechanism the PAT already used (D-013);
/// Atalaya still has no secret store of its own and no token ever leaves this machine.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public AccountStore(AppPaths paths) => _path = paths.AuthDat;

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>Reads the account, or null when absent / not decryptable by this user.</summary>
    public GitHubAccount? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            GitHubAccount? account = JsonSerializer.Deserialize<GitHubAccount>(
                Encoding.UTF8.GetString(plain), JsonOptions);
            return string.IsNullOrWhiteSpace(account?.Token) ? null : account;
        }
        catch
        {
            // Corrupt, or written by another Windows user: behave as "not connected".
            return null;
        }
    }

    public void Save(GitHubAccount account)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(account, JsonOptions));
        File.WriteAllBytes(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // Best effort: the in-memory session is cleared regardless.
        }
    }
}
