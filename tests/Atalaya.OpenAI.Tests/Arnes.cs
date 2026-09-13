using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.OpenAI.Tests;

/// <summary>
/// <b>Por dónde se le enchufa el endpoint falso al proveedor.</b>
/// <para>
/// El andamio dejó el proveedor con la costura de la configuración y la de la clave, pero no la
/// del transporte: ésa la escribe el frente del transporte, y estos tests se escriben a la vez que
/// él. Así que se busca por reflexión —un constructor que acepte un <c>HttpMessageHandler</c>, un
/// <c>HttpClient</c> o un <see cref="IChatEndpoint"/> ya montado— en vez de fijar hoy un nombre que
/// todavía no existe, que es lo que haría que estos tests no compilaran y se llevara por delante la
/// suite entera.
/// </para>
/// <para>
/// Mientras esa costura no esté, todo lo de aquí sale ROJO con el motivo escrito. Es lo correcto:
/// un test de regla que pasara sin la implementación no estaría probando la regla.
/// </para>
/// </summary>
internal static class Arnes
{
    private const BindingFlags Todos =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static OpenAiCompatibleProvider Proveedor(
        EndpointFalso falso, OpenAiEndpoint config, string? clave, ILogger? log = null)
    {
        Dictionary<Type, object?> bolsa = Bolsa(falso, config, clave, log ?? NullLogger.Instance);

        foreach (ConstructorInfo ctor in typeof(OpenAiCompatibleProvider)
                     .GetConstructors(Todos)
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            ParameterInfo[] parametros = ctor.GetParameters();
            if (LlevaTransporte(parametros) && TryFill(parametros, bolsa, out object?[] args))
            {
                return (OpenAiCompatibleProvider)ctor.Invoke(args);
            }
        }

        throw new InvalidOperationException(
            "PROV-3 (frente A) — `OpenAiCompatibleProvider` todavía no tiene por dónde recibir el "
            + "transporte, así que no hay forma de hablarle al endpoint falso. Los tests de regla "
            + "esperan una costura que acepte un `HttpMessageHandler`, un `HttpClient` o un "
            + "`IChatEndpoint` ya montado, junto a las dos que ya están (`Func<OpenAiEndpoint>` y "
            + "`Func<string?>`). Hoy el único constructor es "
            + "(Func<OpenAiEndpoint>, Func<string?>, ILogger?).");
    }

    private static Dictionary<Type, object?> Bolsa(
        EndpointFalso falso, OpenAiEndpoint config, string? clave, ILogger log)
    {
        var bolsa = new Dictionary<Type, object?>
        {
            [typeof(Func<OpenAiEndpoint>)] = new Func<OpenAiEndpoint>(() => config),
            [typeof(Func<string?>)] = new Func<string?>(() => clave),
            [typeof(OpenAiEndpoint)] = config,
            [typeof(string)] = clave,
            [typeof(ILogger)] = log,
            [typeof(HttpMessageHandler)] = falso,
            [typeof(Func<HttpMessageHandler>)] = new Func<HttpMessageHandler>(() => falso),
            [typeof(HttpClient)] = new HttpClient(falso, disposeHandler: false),
            [typeof(Func<HttpClient>)] = new Func<HttpClient>(() => new HttpClient(falso, false)),
            [typeof(TimeSpan)] = TimeSpan.FromSeconds(30),
            [typeof(Func<TimeSpan>)] = new Func<TimeSpan>(() => TimeSpan.FromSeconds(30)),
        };

        IChatEndpoint? endpoint = Endpoint(falso, config, clave, log);
        if (endpoint is not null)
        {
            bolsa[typeof(IChatEndpoint)] = endpoint;
            bolsa[typeof(Func<IChatEndpoint>)] = new Func<IChatEndpoint>(() => endpoint);
        }

        return bolsa;
    }

    /// <summary>La implementación de <see cref="IChatEndpoint"/> que haya, montada sobre el falso.</summary>
    private static IChatEndpoint? Endpoint(
        EndpointFalso falso, OpenAiEndpoint config, string? clave, ILogger log)
    {
        var bolsa = new Dictionary<Type, object?>
        {
            [typeof(Func<OpenAiEndpoint>)] = new Func<OpenAiEndpoint>(() => config),
            [typeof(Func<string?>)] = new Func<string?>(() => clave),
            [typeof(OpenAiEndpoint)] = config,
            [typeof(string)] = clave,
            [typeof(ILogger)] = log,
            [typeof(HttpMessageHandler)] = falso,
            [typeof(Func<HttpMessageHandler>)] = new Func<HttpMessageHandler>(() => falso),
            [typeof(HttpClient)] = new HttpClient(falso, disposeHandler: false),
            [typeof(Func<HttpClient>)] = new Func<HttpClient>(() => new HttpClient(falso, false)),
            [typeof(TimeSpan)] = TimeSpan.FromSeconds(30),
            [typeof(Func<TimeSpan>)] = new Func<TimeSpan>(() => TimeSpan.FromSeconds(30)),
        };

        foreach (Type tipo in typeof(IChatEndpoint).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IChatEndpoint).IsAssignableFrom(t)))
        {
            foreach (ConstructorInfo ctor in tipo.GetConstructors(Todos)
                         .OrderByDescending(c => c.GetParameters().Length))
            {
                ParameterInfo[] parametros = ctor.GetParameters();
                if (LlevaTransporte(parametros) && TryFill(parametros, bolsa, out object?[] args))
                {
                    return (IChatEndpoint)ctor.Invoke(args);
                }
            }
        }

        return null;
    }

    private static bool LlevaTransporte(ParameterInfo[] parametros)
        => parametros.Any(p =>
            p.ParameterType == typeof(HttpMessageHandler)
            || p.ParameterType == typeof(HttpClient)
            || p.ParameterType == typeof(Func<HttpMessageHandler>)
            || p.ParameterType == typeof(Func<HttpClient>)
            || p.ParameterType == typeof(IChatEndpoint)
            || p.ParameterType == typeof(Func<IChatEndpoint>));

    private static bool TryFill(
        ParameterInfo[] parametros, IReadOnlyDictionary<Type, object?> bolsa, out object?[] args)
    {
        args = new object?[parametros.Length];
        for (int i = 0; i < parametros.Length; i++)
        {
            ParameterInfo p = parametros[i];
            if (bolsa.TryGetValue(p.ParameterType, out object? valor))
            {
                args[i] = valor;
                continue;
            }

            object? compatible = bolsa
                .Where(par => par.Value is not null && p.ParameterType.IsInstanceOfType(par.Value))
                .Select(par => par.Value)
                .FirstOrDefault();

            if (compatible is not null)
            {
                args[i] = compatible;
                continue;
            }

            if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
                continue;
            }

            return false;
        }

        return true;
    }
}
