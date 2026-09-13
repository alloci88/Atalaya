using Microsoft.Extensions.Logging;

namespace Atalaya.Agents;

/// <summary>
/// <b>Lo que un proveedor necesita del anfitrión, sin que el anfitrión sepa quién lo pide</b>
/// (PROV-2 §1).
/// <para>
/// <b>Por qué existe.</b> Hasta aquí la aplicación construía los proveedores a mano, con una
/// lambda por casa leyendo el ajuste que esa casa usaba. Eso obligaba a <c>Atalaya.App</c> a
/// referenciar el proyecto de cada proveedor —y con Copilot, a arrastrar su SDK entero—, así que
/// añadir una casa tercera era tocar la aplicación. Ahora la aplicación rellena ESTA costura, que
/// no nombra a nadie, y quien sabe montar cada proveedor es <c>Atalaya.Providers</c>.
/// </para>
/// <para>
/// <b>Ningún nombre de casa en los parámetros.</b> Lo que es de un proveedor concreto se pide
/// <b>por identificador de proveedor</b> —el modelo, el plazo, el directorio de trabajo, el
/// registro donde escribe—; lo que es del anfitrión y no de nadie —la credencial de la cuenta, el
/// puente de herramientas que viaja con la aplicación— se pide sin más. Ese reparto es la regla:
/// un parámetro llamado «el directorio de Fulano» devolvería el acoplamiento por la puerta de
/// atrás.
/// </para>
/// <para>
/// Todo son <b>funciones</b>, no valores, y por la razón de BUGFIX-AJUSTES: capturar el modelo o el
/// plazo al construir hacía que cambiarlos en Ajustes no sirviera de nada hasta reiniciar.
/// </para>
/// </summary>
/// <param name="Model">
/// El modelo configurado para ese proveedor, o vacío para que use el suyo por defecto.
/// </param>
/// <param name="SendTimeout">
/// Cuánto se espera como mucho una respuesta de ese proveedor.
/// </param>
/// <param name="WorkDirectory">
/// Una carpeta propia y estable donde ese proveedor puede escribir lo suyo (su configuración de
/// herramientas, su prompt de sistema). Es del proveedor, así que se pide por su identificador.
/// </param>
/// <param name="AccountToken">
/// La credencial de la cuenta del anfitrión, o null si no hay cuenta conectada. Se lee en cada
/// arranque: conectar o cambiar de cuenta surte efecto sin reiniciar.
/// </param>
/// <param name="AccountLogin">
/// Con qué identidad ha iniciado sesión esa cuenta, o null.
/// </param>
/// <param name="AccountDirectory">
/// Dónde vive el llavero de esa cuenta, <b>si el anfitrión lo tiene fijado a mano</b>; vacío —lo
/// normal— significa «donde lo deje quien lo escribió». Va con <see cref="AccountToken"/> y
/// <see cref="AccountLogin"/> y no con <see cref="WorkDirectory"/> a propósito: no es el
/// directorio de trabajo de un proveedor, es el de la cuenta del anfitrión, y por eso no se pide
/// por identificador.
/// </param>
/// <param name="BridgeExecutable">
/// El ejecutable del puente de herramientas que viaja en la carpeta de la aplicación, para el
/// proveedor que hable por él.
/// </param>
/// <param name="Logger">
/// Dónde escribe ese proveedor. La categoría es su identificador, que es el único nombre suyo que
/// esta costura conoce.
/// </param>
/// <param name="Setting">
/// Un ajuste de ESE proveedor, por su identificador y el nombre del ajuste —la URL base, cómo
/// presenta su clave—, o null si no está puesto (PROV-3 §2). Genérico a propósito: un proveedor por
/// API necesita cosas que ni Copilot ni un CLI necesitan, y añadir un miembro a la costura por cada
/// una acabaría con una costura que enumera las casas, que es lo que PROV-2 vino a quitar.
/// </param>
/// <param name="Secret">
/// <b>El secreto de ese proveedor</b>, del almacén cifrado de la máquina, o null (PROV-3 §3). Va
/// aparte de <see cref="Setting"/> y no como un ajuste más <b>justo para que no pueda acabar en
/// `settings.json` por descuido</b>: lo que se pide por aquí es lo que no se escribe en claro.
/// </param>
public sealed record AgentHostServices(
    Func<string, string?> Model,
    Func<string, TimeSpan> SendTimeout,
    Func<string, string> WorkDirectory,
    Func<string?> AccountToken,
    Func<string?> AccountLogin,
    Func<string?> AccountDirectory,
    Func<string?> BridgeExecutable,
    Func<string, ILogger> Logger,
    Func<string, string, string?> Setting,
    Func<string, string?> Secret);
