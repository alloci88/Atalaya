namespace Atalaya.App.Services;

/// <summary>
/// The canonical, actionable diagnostics for the connection flow (D2.3). Every failing step of
/// the chained verification shows one of these instead of a raw HTTP/git/SDK error. Kept in one
/// place so the README table and the UI say exactly the same thing.
/// </summary>
public static class ConnectionHelp
{
    public const string DocsOAuthPolicy =
        "https://docs.github.com/es/organizations/managing-oauth-access-to-your-organizations-data/about-oauth-app-access-restrictions";

    public const string DocsSamlSso =
        "https://docs.github.com/es/authentication/authenticating-with-saml-single-sign-on/about-authentication-with-saml-single-sign-on";

    public const string DocsCopilotSeat = "https://github.com/settings/copilot";

    /// <summary>Authorize disabled / app not approved by the organization.</summary>
    public const string OrgPolicyBlocked =
        "Tu organización restringe las OAuth Apps. Pulsa «Request» en «Organization access» al autorizar, "
        + "o pide a un owner que apruebe la app «Atalaya».";

    /// <summary>The org enforces SAML and this token has no active SSO session.</summary>
    public const string SamlRequired =
        "Entra primero en github.com y completa el SSO de la organización, luego reintenta.";

    /// <summary>401 anywhere: the token was revoked or expired.</summary>
    public const string TokenRejected =
        "GitHub ha rechazado tus credenciales (revocadas o caducadas). Vuelve a conectar tu cuenta.";

    /// <summary>No network.</summary>
    public const string Offline =
        "Sin conexión con GitHub. Comprueba la red o el proxy y pulsa «Reintentar».";

    /// <summary>The deployment has no client id yet (administrator prerequisite).</summary>
    public const string NoClientId =
        "Este despliegue no tiene configurado el client id de la OAuth App «Atalaya». "
        + "Un administrador debe registrarla (README → anexo «Registrar la OAuth App») y rellenar "
        + "«gitHubClientId» en appsettings.deploy.json junto al ejecutable.";

    /// <summary>The deployment has no hub URL.</summary>
    public const string NoHubUrl =
        "Este despliegue no tiene configurada la URL del hub. Revisa appsettings.deploy.json "
        + "junto al ejecutable.";

    /// <summary>Not a member of the configured organization.</summary>
    public static string NotAnOrgMember(string login, string org)
        => $"Tu cuenta {login} no pertenece a la organización {org}. Pide acceso a un owner y reintenta.";

    /// <summary>The token authenticates but git refuses the hub repository.</summary>
    public static string HubAccessDenied(string login)
        => $"GitHub ha aceptado tu cuenta ({login}) pero no te da acceso al repositorio del hub. "
        + "Si tu organización restringe las OAuth Apps, aprueba «Atalaya» para la organización; "
        + "si usa SAML, completa el SSO en github.com y reintenta.";
}
