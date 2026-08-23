using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Atalaya.App.Services;

/// <summary>The device-code grant GitHub hands us to show the user (D2).</summary>
public sealed record DeviceCodeGrant(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    TimeSpan ExpiresIn,
    TimeSpan Interval);

/// <summary>The OAuth error codes the device flow can return.</summary>
public enum DeviceFlowError
{
    None = 0,

    /// <summary>The user has not finished authorizing yet: keep polling.</summary>
    AuthorizationPending,

    /// <summary>We polled too fast: back off by 5 s and keep polling.</summary>
    SlowDown,

    /// <summary>The device code expired (default 15 min): start over.</summary>
    ExpiredToken,

    /// <summary>The user pressed "Cancel" on github.com.</summary>
    AccessDenied,

    /// <summary>Device flow is not enabled on the OAuth App, or the client id is wrong.</summary>
    DeviceFlowDisabled,

    Unknown,
}

/// <summary>One poll of the token endpoint.</summary>
public sealed record DeviceTokenResult(string? AccessToken, DeviceFlowError Error, string? Description)
{
    public bool Succeeded => !string.IsNullOrEmpty(AccessToken);
}

/// <summary>Raised when the device flow ends without a token (expired, denied, misconfigured).</summary>
public sealed class DeviceFlowException : Exception
{
    public DeviceFlowException(DeviceFlowError error, string message) : base(message) => Error = error;

    public DeviceFlowError Error { get; }
}

/// <summary>Progress of the wait, so the UI can render the expiry countdown (D2).</summary>
public sealed record DeviceFlowProgress(TimeSpan Remaining);

/// <summary>
/// GitHub OAuth **device flow** (D2): authenticate a desktop app with no client secret.
/// <c>POST /login/device/code</c> → show the user code → poll
/// <c>POST /login/oauth/access_token</c> with grant
/// <c>urn:ietf:params:oauth:grant-type:device_code</c>, honouring <c>interval</c> and handling
/// <c>authorization_pending</c> / <c>slow_down</c> / <c>expired_token</c>.
/// <para>
/// The HTTP handler and the delay function are injectable so the whole state machine is tested
/// without network or real waiting.
/// </para>
/// </summary>
public sealed class GitHubDeviceFlow
{
    /// <summary>repo (git over the private hub) + read:org (membership) + read:user (profile).</summary>
    public const string Scopes = "repo read:org read:user";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    /// <summary>Where we send the user to type the code.</summary>
    public const string DefaultVerificationUri = "https://github.com/login/device";

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _deviceCodeUrl;
    private readonly string _accessTokenUrl;

    public GitHubDeviceFlow(
        HttpClient? http = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null,
        string? deviceCodeUrl = null,
        string? accessTokenUrl = null)
    {
        _http = http ?? new HttpClient();
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _deviceCodeUrl = deviceCodeUrl ?? DeviceCodeUrl;
        _accessTokenUrl = accessTokenUrl ?? AccessTokenUrl;
    }

    /// <summary>Step 1: ask GitHub for a device code + the user code we display.</summary>
    public async Task<DeviceCodeGrant> RequestCodeAsync(string clientId, CancellationToken ct, string scopes = Scopes)
    {
        using JsonDocument doc = await PostFormAsync(
            _deviceCodeUrl,
            new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = scopes },
            ct);

        JsonElement root = doc.RootElement;
        if (ReadString(root, "error") is { Length: > 0 } error)
        {
            throw new DeviceFlowException(MapError(error), Describe(MapError(error), ReadString(root, "error_description")));
        }

        string? deviceCode = ReadString(root, "device_code");
        string? userCode = ReadString(root, "user_code");
        if (deviceCode is null || userCode is null)
        {
            throw new DeviceFlowException(DeviceFlowError.Unknown,
                "GitHub no devolvió un código de dispositivo. Reintenta en unos segundos.");
        }

        return new DeviceCodeGrant(
            deviceCode,
            userCode,
            ReadString(root, "verification_uri") ?? DefaultVerificationUri,
            TimeSpan.FromSeconds(ReadInt(root, "expires_in") ?? 900),
            TimeSpan.FromSeconds(Math.Max(1, ReadInt(root, "interval") ?? 5)));
    }

    /// <summary>Step 2 (one attempt): exchange the device code for a user token.</summary>
    public async Task<DeviceTokenResult> PollOnceAsync(string clientId, string deviceCode, CancellationToken ct)
    {
        using JsonDocument doc = await PostFormAsync(
            _accessTokenUrl,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            },
            ct);

        JsonElement root = doc.RootElement;
        if (ReadString(root, "access_token") is { Length: > 0 } token)
        {
            return new DeviceTokenResult(token, DeviceFlowError.None, null);
        }

        string? error = ReadString(root, "error");
        DeviceFlowError mapped = error is null ? DeviceFlowError.Unknown : MapError(error);
        return new DeviceTokenResult(null, mapped, ReadString(root, "error_description"));
    }

    /// <summary>
    /// Step 2 (the loop): polls until the user authorizes, honouring <c>interval</c> and adding
    /// 5 s on every <c>slow_down</c>. Throws <see cref="DeviceFlowException"/> when the code
    /// expires or the user denies; cancellable at any point.
    /// </summary>
    public async Task<string> WaitForTokenAsync(
        string clientId,
        DeviceCodeGrant grant,
        IProgress<DeviceFlowProgress>? progress,
        CancellationToken ct)
    {
        DateTimeOffset deadline = _now() + grant.ExpiresIn;
        TimeSpan interval = grant.Interval;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DeviceFlowProgress(Max(TimeSpan.Zero, deadline - _now())));

            await _delay(interval, ct);

            if (_now() >= deadline)
            {
                throw new DeviceFlowException(DeviceFlowError.ExpiredToken,
                    Describe(DeviceFlowError.ExpiredToken, null));
            }

            DeviceTokenResult result = await PollOnceAsync(clientId, grant.DeviceCode, ct);
            if (result.Succeeded)
            {
                return result.AccessToken!;
            }

            switch (result.Error)
            {
                case DeviceFlowError.AuthorizationPending:
                    break;
                case DeviceFlowError.SlowDown:
                    // Per the spec the client must add 5 s to its interval on every slow_down.
                    interval += TimeSpan.FromSeconds(5);
                    break;
                default:
                    throw new DeviceFlowException(result.Error, Describe(result.Error, result.Description));
            }

            progress?.Report(new DeviceFlowProgress(Max(TimeSpan.Zero, deadline - _now())));
        }
    }

    /// <summary>Human, actionable text for each terminal error (D2 diagnostics).</summary>
    public static string Describe(DeviceFlowError error, string? description) => error switch
    {
        DeviceFlowError.ExpiredToken =>
            "El código ha caducado antes de que se completara la autorización. Pulsa «Conectar con GitHub» "
            + "para pedir uno nuevo.",
        DeviceFlowError.AccessDenied =>
            "Has cancelado la autorización en github.com. Si el botón «Authorize» aparecía deshabilitado, "
            + "es la política de OAuth Apps de tu organización: pulsa «Request» en «Organization access» "
            + "o pide a un owner que apruebe la app «Atalaya».",
        DeviceFlowError.DeviceFlowDisabled =>
            "La OAuth App configurada no tiene habilitado el device flow, o el client id no es válido. "
            + "Revisa el registro de la app (README → anexo «Registrar la OAuth App»).",
        _ => string.IsNullOrWhiteSpace(description)
            ? "GitHub rechazó la autenticación. Reintenta en unos segundos."
            : $"GitHub rechazó la autenticación: {description}",
    };

    private static DeviceFlowError MapError(string error) => error switch
    {
        "authorization_pending" => DeviceFlowError.AuthorizationPending,
        "slow_down" => DeviceFlowError.SlowDown,
        "expired_token" => DeviceFlowError.ExpiredToken,
        "access_denied" => DeviceFlowError.AccessDenied,
        "device_flow_disabled" or "unauthorized_client" or "incorrect_client_credentials"
            => DeviceFlowError.DeviceFlowDisabled,
        _ => DeviceFlowError.Unknown,
    };

    private async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Atalaya");

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw new DeviceFlowException(DeviceFlowError.Unknown,
                $"Respuesta inesperada de GitHub ({(int)response.StatusCode}).");
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out int n) => n,
            _ => null,
        };
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
