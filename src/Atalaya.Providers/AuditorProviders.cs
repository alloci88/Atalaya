using Atalaya.Agents;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.OpenAI;
using Microsoft.Extensions.Logging;

namespace Atalaya.Providers;

/// <summary>
/// <b>Quién puede auditar en esta versión, y en qué orden</b> (PROV-2 §1).
/// <para>
/// <b>Por qué existe este proyecto entero para una función.</b> Hasta PROV-2 los dos proveedores
/// se construían dentro de <c>App.xaml.cs</c>, cada uno con su lambda leyendo sus ajustes. Eso
/// obligaba a <c>Atalaya.App</c> a referenciar el proyecto de cada casa, y con Copilot eso
/// significa arrastrar su SDK a la aplicación entera: PROV-1 midió que ése era el motivo por el
/// que un tercer proveedor no cabía sin tocar media aplicación. La composición es la única parte
/// que de verdad necesita conocerlos a todos, así que vive sola, y la aplicación solo conoce
/// <see cref="AgentHostServices"/> y el registro.
/// </para>
/// <para>
/// <b>Los identificadores viven donde viven las casas.</b> Todo lo que aquí se pide al anfitrión
/// se pide con la constante <c>Id</c> del propio proveedor —nunca con la cadena escrita a mano—,
/// que es la regla que vigila el barrido de <c>ProviderDecouplingTests</c>.
/// </para>
/// </summary>
public static class AuditorProviders
{
    /// <summary>
    /// Los proveedores de esta versión, <b>en el orden que ve el usuario</b>.
    /// <para>
    /// El orden no es un detalle de registro: el primero es el que la pantalla Cuenta enseña
    /// arriba y el que Ajustes ofrece primero. Se listan uno a uno, y no se recolectan por su
    /// interfaz, justo para que ese orden no dependa de en qué línea quedó registrado cada uno.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IAuditorProvider> Build(AgentHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);

        // Copilot: el agente real del SDK, autenticado con la credencial de la cuenta del
        // anfitrión (D3) y corriendo siempre el CLI que viene en el paquete del SDK. El llavero
        // se lee en cada arranque, así que conectar o cambiar de cuenta surte efecto sin
        // reiniciar; sin credencial el adaptador cae al comportamiento anterior a F2.
        var copilot = new RealCopilotAgent(
            baseDirectory: host.AccountDirectory(),
            logger: host.Logger(RealCopilotAgent.Id),
            modelProvider: () => host.Model(RealCopilotAgent.Id),
            sendTimeout: () => host.SendTimeout(RealCopilotAgent.Id),
            tokenProvider: host.AccountToken,
            loginProvider: host.AccountLogin);

        // Claude Code (F14): el CLI que el usuario ya tiene. Atalaya no lo instala ni guarda
        // credenciales suyas — lo busca en el PATH y usa la sesión que el CLI tenga iniciada. El
        // puente MCP viaja en la carpeta de la aplicación y es lo que `claude` lanza como
        // servidor de herramientas.
        var claudeCode = new ClaudeCodeProvider(
            bridgeExecutable: host.BridgeExecutable() ?? string.Empty,
            modelProvider: () => host.Model(ClaudeCodeProvider.Id),
            workDirectory: () => host.WorkDirectory(ClaudeCodeProvider.Id),
            logger: host.Logger(ClaudeCodeProvider.Id));

        // PROV-3: el primero por API. No hay nada que instalar ni ningún login de máquina — una
        // URL, un modelo y una clave—, y con eso habla con las siete casas que usan este dialecto,
        // las locales incluidas. Va el último porque es el que hay que configurar; los dos de
        // arriba funcionan con lo que la máquina ya tiene.
        var openAi = new OpenAiCompatibleProvider(
            endpoint: () => new OpenAiEndpoint(
                BaseUrl: host.Setting(OpenAiCompatibleProvider.Id, OpenAiSettingKeys.BaseUrl) ?? string.Empty,
                Model: host.Model(OpenAiCompatibleProvider.Id) ?? string.Empty,
                Auth: OpenAiSettingKeys.AuthOf(host.Setting(OpenAiCompatibleProvider.Id, OpenAiSettingKeys.Auth))),
            key: () => host.Secret(OpenAiCompatibleProvider.Id),
            logger: host.Logger(OpenAiCompatibleProvider.Id));

        return new IAuditorProvider[] { copilot, claudeCode, openAi };
    }

    /// <summary>
    /// <b>Cómo se monta un transporte para la casa por API</b> (PROV-3 §2 y §8).
    /// <para>
    /// Está aquí y no en la aplicación por lo mismo que <see cref="Build"/>: construir un
    /// <c>OpenAiClient</c> es conocer a una casa, y este proyecto es el único que las conoce. La
    /// aplicación pide el verbo y lo llama sin saber qué hay detrás.
    /// </para>
    /// <para>
    /// <b>El manejador se construye UNA vez</b> y se reparte: uno por llamada agota los puertos del
    /// sistema. Lleva el proxy del sistema explícito y la revocación de TLS según el ajuste de la
    /// máquina, que es D-047 traído a HTTP.
    /// </para>
    /// </summary>
    public static ChatEndpointFactory ChatEndpoints(
        bool requireTlsRevocationCheck, Func<string, ILogger> logger)
    {
        HttpMessageHandler handler = OpenAiHttp.CreateHandler(requireTlsRevocationCheck);

        return (endpoint, key) => new OpenAiClient(
            () => endpoint,
            () => key,
            handler,
            logger(OpenAiCompatibleProvider.Id));
    }
}
