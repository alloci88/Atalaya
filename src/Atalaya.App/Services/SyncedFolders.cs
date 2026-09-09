namespace Atalaya.App.Services;

/// <summary>
/// ¿Vive esta carpeta dentro de un cliente de sincronización? (BUGFIX-SYNC).
/// <para>
/// No es una curiosidad: en un equipo corporativo el Escritorio y Documentos están redirigidos a
/// <b>OneDrive</b>, así que «descomprímelo donde quieras» acaba, la mayoría de las veces, dentro
/// de una carpeta sincronizada. Y ahí el cliente mantiene manejadores abiertos sobre los ficheros
/// mientras los sube: un movimiento masivo —que es exactamente lo que hace una actualización— se
/// topa con «acceso denegado» de forma intermitente.
/// </para>
/// <para>
/// Saberlo no cambia lo que se intenta, cambia <b>lo que se dice</b>. «Access denied» no le dice a
/// nadie qué hacer; «estás dentro de OneDrive, pausa la sincronización o mueve la carpeta» sí. Y
/// nunca prohíbe nada: la mayoría de los días la actualización funcionará igualmente, y bloquearla
/// por precaución sería castigar a todo el mundo por un fallo intermitente.
/// </para>
/// </summary>
public static class SyncedFolders
{
    /// <summary>
    /// Las variables que planta el propio cliente de OneDrive. Es la detección <b>fiable</b>:
    /// funciona aunque la carpeta se llame de otra manera o cuelgue de una ruta rara, que es
    /// justo lo que hace un despliegue corporativo con su tenant.
    /// </summary>
    private static readonly string[] OneDriveVariables =
    {
        "OneDrive", "OneDriveCommercial", "OneDriveConsumer",
    };

    /// <summary>
    /// Qué cliente de sincronización, o null si ninguno.
    /// </summary>
    /// <param name="path">La carpeta a mirar. Normalmente, la de la instalación.</param>
    /// <param name="environment">
    /// De dónde se leen las variables de entorno. Se inyecta para los tests: probar esto
    /// escribiendo en el entorno del proceso contaminaría a los demás tests que corren a la vez.
    /// </param>
    public static string? Detect(string? path, Func<string, string?>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            full = path;
        }

        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;
        foreach (string variable in OneDriveVariables)
        {
            if (read(variable) is { Length: > 0 } root && IsUnder(full, root))
            {
                return "OneDrive";
            }
        }

        // Y si no hay variable —Dropbox y Google Drive no ponen ninguna estándar, y OneDrive
        // tampoco la pone para una sesión de otro usuario—, el nombre de la carpeta raíz, que es
        // lo que el propio cliente crea y la gente no suele renombrar.
        foreach (string segment in full.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (ByName(segment) is { } provider)
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// La línea del banner: avisa sin estorbar. No bloquea la actualización porque muchos días
    /// funcionará igualmente; solo pone el nombre del sospechoso encima de la mesa <b>antes</b> de
    /// que falle, para que el fallo no sea una sorpresa.
    /// </summary>
    public static string Note(string? path, Func<string, string?>? environment = null)
        => Detect(path, environment) is { } provider
            ? $"Atalaya está instalada dentro de {provider}: si la actualización falla, pausa la "
              + "sincronización y reintenta."
            : string.Empty;

    /// <summary>
    /// La receta que acompaña a un fallo. Dos salidas, y las dos funcionan: la de ahora mismo
    /// —pausar— y la definitiva —mover la carpeta—, que además ahorra resubir el paquete entero
    /// en cada actualización.
    /// </summary>
    public static string Advice(string? path, Func<string, string?>? environment = null)
        => Detect(path, environment) is { } provider
            ? $"Atalaya está dentro de {provider}, y su sincronización retiene los ficheros "
              + "mientras los sube: pausa la sincronización y reintenta, o mueve Atalaya a una "
              + @"carpeta no sincronizada (por ejemplo C:\Apps\Atalaya)."
            : string.Empty;

    /// <summary>
    /// El nombre de una carpeta raíz de sincronización. Se aceptan las formas con sufijo —
    /// «OneDrive - Acme», «Dropbox (Empresa)»— porque es como las nombran los clientes cuando la
    /// cuenta es de una organización, que es el caso que trae este arreglo.
    /// </summary>
    private static string? ByName(string segment)
    {
        if (Is(segment, "OneDrive"))
        {
            return "OneDrive";
        }

        if (Is(segment, "Dropbox"))
        {
            return "Dropbox";
        }

        if (Is(segment, "Google Drive") || Is(segment, "GoogleDrive") || Is(segment, "Mi unidad")
            || Is(segment, "My Drive"))
        {
            return "Google Drive";
        }

        return null;

        static bool Is(string name, string root)
            => name.Equals(root, StringComparison.OrdinalIgnoreCase)
               || (name.Length > root.Length
                   && name.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && (name[root.Length] == ' ' || name[root.Length] == '-' || name[root.Length] == '('));
    }

    /// <summary>¿Cuelga <paramref name="path"/> de <paramref name="root"/> (o es él mismo)?</summary>
    private static bool IsUnder(string path, string root)
    {
        try
        {
            string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return path.TrimEnd(Path.DirectorySeparatorChar)
                       .Equals(full, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Una variable con una ruta inválida no es motivo para no poder actualizar.
            return false;
        }
    }
}
