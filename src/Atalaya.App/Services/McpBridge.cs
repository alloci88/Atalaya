using System.Runtime.InteropServices;

namespace Atalaya.App.Services;

/// <summary>
/// Dónde está <c>Atalaya.Mcp</c>, el puente que el CLI de <c>claude</c> lanza como servidor MCP
/// (F14).
/// <para>
/// Viaja en la misma carpeta que la aplicación —el <c>.csproj</c> lo referencia con
/// <c>ReferenceOutputAssembly=false</c> justo para eso: constrúyelo y cópialo, pero no lo enlaces—
/// así que resolverlo es mirar al lado. Se hace en un método y no con un literal repartido porque
/// lo necesitan el proveedor real y los tests, y dos copias de una ruta son una que se queda vieja.
/// </para>
/// </summary>
public static class McpBridge
{
    /// <summary>El nombre del ejecutable en esta plataforma.</summary>
    public static string FileName
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Atalaya.Mcp.exe" : "Atalaya.Mcp";

    /// <summary>
    /// La ruta junto a la aplicación. Se devuelve aunque el fichero no exista: quien falla
    /// entonces es el lanzamiento del CLI, y su error —«el servidor MCP no conectó»— ya dice la
    /// verdad y para la sesión antes de gastar. Inventar aquí un fallo distinto solo añadiría un
    /// segundo sitio donde diagnosticar lo mismo.
    /// </summary>
    public static string ResolvePath(string? baseDirectory = null)
        => Path.Combine(baseDirectory ?? AppContext.BaseDirectory, FileName);
}
