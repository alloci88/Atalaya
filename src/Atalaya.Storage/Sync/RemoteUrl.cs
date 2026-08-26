namespace Atalaya.Storage.Sync;

/// <summary>
/// «¿Estas dos URLs son el MISMO repositorio?» — la única regla, escrita una vez (F5.8 §2).
/// <para>
/// Existía ya dentro de <see cref="HubSyncService"/> para decidir si re-apuntar <c>origin</c>
/// cuando el despliegue mueve el hub. Vincular un clon local hace exactamente la misma pregunta
/// —¿el remoto de esta carpeta es el repo de esta app?— y esa comparación no puede tener dos
/// implementaciones: una más estricta que la otra rechazaría carpetas correctas o aceptaría
/// carpetas equivocadas según por dónde se entrara.
/// </para>
/// <para>
/// <b>Qué se considera igual.</b> Mayúsculas, la barra final y el <c>.git</c> final no distinguen
/// un repo de otro. Tampoco la FORMA de clonarlo: <c>https://github.com/org/repo.git</c>,
/// <c>git@github.com:org/repo</c> y <c>ssh://git@github.com/org/repo</c> son el mismo repositorio,
/// y un compañero que clonó por SSH tiene su clon tan válido como quien clonó por HTTPS. Las
/// credenciales embebidas en la URL (<c>https://x-access-token:TOKEN@host/…</c>) tampoco cuentan
/// — son de quien clona, no del repo.
/// </para>
/// </summary>
public static class RemoteUrl
{
    /// <summary>True si las dos URLs apuntan al mismo repositorio.</summary>
    public static bool Same(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase)
           && Normalize(a).Length > 0;

    /// <summary>
    /// La forma canónica <c>host/ruta</c>, sin esquema, sin credenciales, sin <c>.git</c> y sin
    /// barra final. Vacía cuando no hay URL: dos ausencias no son «el mismo repo».
    /// </summary>
    public static string Normalize(string? url)
    {
        string u = (url ?? string.Empty).Trim();
        if (u.Length == 0)
        {
            return string.Empty;
        }

        // Rutas locales: una ruta de Windows con barras invertidas y la misma con barras
        // normales son LA MISMA carpeta, y git guarda una u otra según por dónde se le
        // pasara. Sin esto, vincular un clon local hecho a mano fallaría contra el mismo
        // repo escrito al revés.
        u = u.Replace('\\', '/');

        // Esquema: https://, http://, ssh://, git://, file://…
        int scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            u = u[(scheme + 3)..];
        }

        // Credenciales embebidas (usuario, o usuario:token) antes del host.
        int at = u.IndexOf('@');
        if (at >= 0)
        {
            u = u[(at + 1)..];
        }

        // Forma scp de SSH: host:org/repo → host/org/repo. Solo cuando lo que sigue a los dos
        // puntos NO es un puerto: `host:2222/org/repo` sí lleva puerto y se deja como está.
        int colon = u.IndexOf(':');
        if (colon >= 0 && !IsPort(u, colon))
        {
            u = string.Concat(u[..colon], "/", u[(colon + 1)..].TrimStart('/'));
        }

        // Barras repetidas: las deja la conversión de arriba y no significan nada.
        while (u.Contains("//", StringComparison.Ordinal))
        {
            u = u.Replace("//", "/", StringComparison.Ordinal);
        }

        u = u.Trim('/');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            u = u[..^4].TrimEnd('/');
        }

        return u;
    }

    private static bool IsPort(string url, int colon)
    {
        int i = colon + 1;
        int digits = 0;
        while (i < url.Length && char.IsAsciiDigit(url[i]))
        {
            i++;
            digits++;
        }

        return digits > 0 && (i == url.Length || url[i] == '/');
    }
}
