using System.Runtime.CompilerServices;
using Atalaya.App.Services;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>El arranque de la suite, para lo único que la aplicación siembra al arrancar</b>
/// (PROV-2 §2).
/// <para>
/// Desde PROV-2 los nombres de las casas no están escritos en <see cref="ProviderNames"/>: se
/// siembran desde el registro en <c>App.StartAsync</c>, y sin sembrar un identificador sale tal
/// cual —que es lo correcto para una casa que esta versión ya no trae—. Los tests que comprueban
/// que el informe, la ficha y Métricas NOMBRAN la casa necesitan por tanto lo mismo que necesita
/// la aplicación: que alguien haya sembrado.
/// </para>
/// <para>
/// Se hace aquí, una vez por ensamblado y antes del primer test, y con LAS DOS CASAS DE VERDAD:
/// sembrar con dobles que repitieran los nombres a mano sería volver a tener el literal en dos
/// sitios, que es justo lo que la entrega quita. Es el equivalente exacto del arranque real —el
/// registro lista estos dos proveedores, en este orden— y no un atajo del arnés.
/// </para>
/// <para>
/// Un test que quiera otra composición —un tercer proveedor, o ninguno— llama a
/// <see cref="ProviderNames.Seed"/> con la suya: el método reemplaza el mapa entero.
/// </para>
/// </summary>
internal static class TestProviderNames
{
    [ModuleInitializer]
    internal static void Seed()
        => ProviderNames.Seed(new IAuditorProvider[]
        {
            new RealCopilotAgent(),
            new ClaudeCodeProvider(string.Empty),
        });
}
