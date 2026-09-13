using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Atalaya.App.Services;

/// <summary>
/// <b>Dónde vive la clave de un proveedor</b> (PROV-3 §3).
/// <para>
/// <b>No en <c>settings.json</c>, y ése es el punto.</b> `settings.json` es texto plano, se abre
/// para mirar un ajuste, se pega en un mensaje cuando algo falla y acaba en una captura. Una clave
/// de API es dinero de quien la tiene: va cifrada, en su propio fichero, con <b>DPAPI de usuario</b>
/// —la misma protección y el mismo sitio que <c>auth.dat</c>, que es donde ya vive la credencial de
/// la cuenta (D-285)—. Otro usuario de Windows no la descifra; copiar el fichero a otra máquina no
/// sirve de nada.
/// </para>
/// <para>
/// <b>Nunca va al hub.</b> El hub es un repositorio git que ve el equipo entero: una clave ahí es
/// una clave publicada. Este fichero vive en <c>%LOCALAPPDATA%</c>, fuera del clon, como todo lo
/// que es de la máquina.
/// </para>
/// <para>
/// Es un mapa por identificador de proveedor y no un campo con nombre de casa, por lo mismo que el
/// mapa de modelos de PROV-2: la casa siguiente no tiene que tocar esto.
/// </para>
/// </summary>
public sealed class ProviderSecretStore
{
    private readonly string _path;

    public ProviderSecretStore(AppPaths paths)
        => _path = System.IO.Path.Combine(paths.Root, "secrets.dat");

    /// <summary>Dónde está el fichero. Lo usa el reset de fábrica, y los tests para comprobarlo.</summary>
    public string Location => _path;

    /// <summary>
    /// El secreto de ese proveedor, o null. <b>No lanza nunca</b>: un fichero de otro usuario, o
    /// corrupto, se lee como «no hay clave» — que es lo que de verdad pasa —, y el proveedor lo
    /// dice en su comprobación en vez de tumbar el arranque.
    /// </summary>
    public string? Get(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        Dictionary<string, string> all = Read();
        return all.TryGetValue(providerId.Trim(), out string? secret) && secret.Length > 0
            ? secret
            : null;
    }

    /// <summary>
    /// Guarda —o borra, con null o vacío— el secreto de ese proveedor. Escribe el fichero entero
    /// cifrado: no hay un camino que deje una clave en claro ni un momento.
    /// </summary>
    public void Set(string providerId, string? secret)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        Dictionary<string, string> all = Read();
        string id = providerId.Trim();

        if (string.IsNullOrWhiteSpace(secret))
        {
            all.Remove(id);
        }
        else
        {
            all[id] = secret.Trim();
        }

        Write(all);
    }

    /// <summary>¿Hay clave guardada para ese proveedor? Sin devolverla, para poder pintar «•••».</summary>
    public bool Has(string providerId) => Get(providerId) is { Length: > 0 };

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain))
                is { } map
                ? new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Otro usuario de Windows, un fichero a medias o un formato que no es el nuestro. No
            // hay clave que dar, y decirlo así es la verdad; reventar aquí dejaría sin arrancar por
            // un fichero que se puede volver a escribir en diez segundos.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Write(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all));
        File.WriteAllBytes(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }
}
