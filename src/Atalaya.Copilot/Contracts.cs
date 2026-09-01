using Atalaya.Agents;

namespace Atalaya.Copilot;

/// <summary>
/// Canonical help texts for the not-ready cases (§6.1, F2.3). Son de ESTA casa: hablan de
/// GitHub, de asientos y de la pantalla Cuenta. Lo que se dice igual con cualquier proveedor
/// vive en <see cref="AuditorHelp"/> (F14).
/// </summary>
public static class CopilotHelp
{
    /// <summary>
    /// The normal path since F2: Copilot authenticates with the account token obtained by the
    /// in-app GitHub login, so the remedy is a click in Atalaya, not a console.
    /// </summary>
    public const string NoAccount =
        "Copilot no está autenticado. Ve a Cuenta y pulsa «Conectar con GitHub»: "
        + "el mismo login habilita el hub y tu asiento de Copilot.";

    /// <summary>The account token was accepted but the account has no Copilot seat.</summary>
    public const string NoSeat =
        "Tu cuenta no tiene asiento de Copilot asignado; pídelo al administrador de la organización "
        + "(github.com/settings/copilot). Esto NO es un problema de autenticación.";

    /// <summary>The credential was rejected (revoked, expirado o SSO caducado).</summary>
    public const string TokenRejected =
        "GitHub ha rechazado tus credenciales (revocadas o caducadas). Ve a Cuenta y vuelve a conectar.";

    /// <summary>
    /// El modelo configurado no sirve. La frase es la común (<see cref="AuditorHelp"/>): dice lo
    /// mismo venga de quien venga, y tener dos copias solo serviría para que una envejeciera.
    /// </summary>
    public static string ModelUnavailable(string? modelId) => AuditorHelp.ModelUnavailable(modelId);

    /// <summary>
    /// La organización se ha quedado sin peticiones premium (BUGFIX-CUOTA). Dice las tres cosas que
    /// faltaban: QUÉ pasa (no es tu cuenta), que en Atalaya NO hay nada que tocar, y las dos únicas
    /// salidas reales. El periodo solo se nombra cuando el propio error lo dice; la fecha exacta del
    /// reset no la trae, así que no se inventa.
    /// </summary>
    public static string QuotaExhausted(string? period)
        => "La organización ha agotado sus AI credits de Copilot"
        + (period is { Length: > 0 } ? $" (el proveedor la describe como cuota {period})" : string.Empty)
        + ". No es tu asiento ni tus credenciales, y no hay nada que arreglar en Atalaya: hay que "
        + "esperar a que se renueve la cuota, o auditar con un modelo de tarifa menor. Atalaya no "
        + "reintenta sola: reintentar contra una cuota agotada gasta los credits del reset "
        + "siguiente.";

    /// <summary>Sin red o con el servicio caído: es transitorio y se puede reintentar.</summary>
    public const string Offline =
        "No hay conexión con GitHub (o su servicio no responde). Comprueba la red o el proxy y "
        + "reintenta: este fallo es transitorio.";

    /// <summary>
    /// Lo que no se ha sabido clasificar. Enseña el error del proveedor ÍNTEGRO en vez de proponer
    /// una causa: un mensaje bonito con la causa equivocada manda a alguien a arreglar lo que no
    /// está roto, que es justo lo que pasó con la cuota (N-2).
    /// </summary>
    public static string Unknown(string raw)
        => "Copilot ha rechazado la operación y Atalaya no reconoce el motivo, así que no se lo "
        + "inventa. Éste es el error tal cual lo ha devuelto el proveedor"
        + (string.IsNullOrWhiteSpace(raw) ? "." : $": {raw}");

    /// <summary>
    /// Legacy fallback text: used only when there is no account token and Atalaya falls back to
    /// the credentials the `copilot` CLI stored on this machine (pre-F2 setup, kept working).
    /// </summary>
    public const string NotAuthenticated =
        "Copilot no está autenticado en esta máquina. Instala el CLI (npm install -g @github/copilot), "
        + "ejecuta `copilot` en una terminal, usa /login una vez con tu cuenta con asiento de Copilot, y reintenta.";
}
