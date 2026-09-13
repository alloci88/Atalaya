using System.Security.Cryptography.X509Certificates;

namespace Atalaya.OpenAI;

/// <summary>
/// <b>Con qué red se sale</b> (PROV-3 §8) — proxy del sistema y la política TLS de esta casa, en un
/// solo sitio.
/// <para>
/// <b>Por qué existe.</b> Atalaya se usa en redes corporativas: se sale por un proxy y la TLS la
/// intercepta un aparato con una raíz propia. El cliente del hub ya vive con eso (D-047), y un
/// proveedor por API que no lo haga se queda mudo detrás del proxy. Esto es el equivalente HTTP de
/// aquella decisión, escrito una vez para que el día que el cliente de GitHub lo necesite tenga de
/// dónde cogerlo en vez de escribirse el suyo.
/// </para>
/// <para>
/// <b>Lo que NO hace, y es deliberado</b>: no lo arma nadie de esta casa. El <c>HttpMessageHandler</c>
/// lo recibe <see cref="OpenAiClient"/> por constructor y lo monta la composición, que es la única
/// que conoce los ajustes de la máquina — y es lo que permite que todos los tests de la entrega
/// corran contra un endpoint dentro del proceso, sin tocar la red.
/// </para>
/// </summary>
public static class OpenAiHttp
{
    /// <summary>
    /// <b>Lo que se espera a que el endpoint EMPIECE a contestar</b>: diez segundos, el tope de
    /// D-208 — o abre, o falla, pero termina.
    /// <para>
    /// Acota la ida: conectar, atravesar el proxy y recibir las cabeceras. <b>No acota el cuerpo</b>,
    /// y no puede: una unidad de auditoría se escribe en minutos y un tope ahí cortaría respuestas
    /// buenas por el mero hecho de ser largas. El cuerpo lo gobierna el token de la sesión, que es
    /// quien sabe cuándo el usuario ha dicho basta.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// La espera corta del <b>único</b> reintento. No es un backoff: no hay un segundo intento que
    /// escalar. Con un 429 delante, insistir deprisa es lo que agota lo que queda.
    /// </summary>
    public static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// El handler con el que se habla con un endpoint de fuera.
    /// <list type="bullet">
    /// <item>
    /// <b>Proxy del sistema.</b> <c>Proxy = null</c> con <c>UseProxy</c> puesto es lo que hace que
    /// .NET use <c>HttpClient.DefaultProxy</c> — en Windows, el proxy configurado en el sistema.
    /// Está escrito y no dado por hecho: es el comportamiento por defecto hoy, y una línea que
    /// dice lo que se quiere sobrevive a que el defecto cambie.
    /// </item>
    /// <item>
    /// <b>Revocación en soft-fail</b>, que es D-047 aplicado a HTTP. Un responder CRL/OCSP
    /// bloqueado es rutina detrás de un proxy que intercepta TLS, y tirar la conexión por no poder
    /// preguntarle cuesta disponibilidad sin comprar seguridad —quien puede interceptar el tráfico
    /// puede igualmente bloquear al responder—. Lo demás se sigue verificando: una raíz no
    /// confiable, un certificado caducado o un nombre que no cuadra se rechazan igual, porque eso
    /// es la validación de cadena normal y no se toca.
    /// </item>
    /// <item>
    /// <b><c>requireTlsRevocationCheck</c> restaura el hard-fail</b>, igual que el ajuste homónimo
    /// se lo restaura al hub. Quien quiera la comprobación estricta la tiene con el mismo
    /// interruptor para las dos salidas.
    /// </item>
    /// </list>
    /// </summary>
    public static HttpMessageHandler CreateHandler(bool requireTlsRevocationCheck)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = null,

            // Un endpoint detrás de un balanceador reparte mal si la conexión vive para siempre.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        handler.SslOptions.CertificateRevocationCheckMode = requireTlsRevocationCheck
            ? X509RevocationMode.Online
            : X509RevocationMode.NoCheck;

        return handler;
    }
}
