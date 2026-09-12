using System.Text;

namespace Atalaya.Agents;

/// <summary>
/// Los textos de ayuda que NO dependen de la casa (F14).
/// <para>
/// Casi toda la ayuda es del proveedor —«ve a Cuenta y conecta con GitHub», «instala Claude Code e
/// inicia sesión»— y vive con él. Lo que baja aquí es lo que se dice IGUAL diga quien lo diga: que
/// el modelo elegido ya no sirve y que se cambia en Ajustes. Duplicar esa frase por proveedor solo
/// conseguiría que una de las copias envejeciera.
/// </para>
/// <para>
/// PROV-2 §1: aquí baja también el formateador del error CRUDO. Lo llamaba la aplicación desde el
/// clasificador del dialecto de una casa concreta —y lo llamaba para fallos que ni siquiera eran
/// del proveedor, como que no hubiera sitio en disco—, así que un fallo de Atalaya se enseñaba con
/// el formato de GitHub por el mero hecho de que ahí estaba escrito el <c>StringBuilder</c>.
/// </para>
/// </summary>
public static class AuditorHelp
{
    /// <summary>
    /// El modelo configurado no sirve. Dice QUÉ pasó, POR QUÉ no es culpa de la red ni de la cuenta,
    /// y DÓNDE se arregla — las tres cosas que faltaban cuando el fallo era mudo (F5.15).
    /// </summary>
    public static string ModelUnavailable(string? modelId) => ModelUnavailableFor(null, modelId);

    /// <summary>
    /// Lo mismo, <b>nombrando al proveedor activo</b> (PROV-2 §1). Quien lea el aviso está mirando
    /// una sesión que iba a correr con alguien concreto: decirle «el proveedor» cuando la propia
    /// pantalla acaba de anunciar con quién se auditaba es la misma clase de vaguedad que el pie
    /// del coste que no decía de quién era.
    /// <para>
    /// El nombre sale de <c>IAuditorProvider.ProviderName</c>, que es quien lo sabe. Sin nombre
    /// —los caminos que no tienen proveedor delante— se dice «el proveedor», que es lo que se decía
    /// antes: es un texto menos preciso, no uno falso.
    /// </para>
    /// </summary>
    public static string ModelUnavailableFor(string? providerName, string? modelId)
    {
        bool named = !string.IsNullOrWhiteSpace(providerName);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return named
                ? $"No se pudo iniciar la sesión: {providerName} ha rechazado el modelo configurado. "
                  + "Elige otro en Ajustes."
                : "No se pudo iniciar la sesión: el proveedor rechazó el modelo configurado. "
                  + "Elige otro en Ajustes.";
        }

        return named
            ? $"No se pudo iniciar: el modelo «{modelId}» no está disponible para tu cuenta de "
              + $"{providerName}. Elige otro en Ajustes."
            : $"No se pudo iniciar: el modelo «{modelId}» no está disponible para tu cuenta. "
              + "Elige otro en Ajustes.";
    }

    /// <summary>
    /// El error tal cual, con el tipo delante y la cadena de causas detrás — <b>sin el nombre de
    /// nadie</b>. Es lo que se copia y se pega cuando hay que reclamar: el identificador de
    /// petición que devuelva quien sea va aquí dentro, y es lo único con lo que se puede buscar
    /// la llamada concreta.
    /// <para>
    /// Vive aquí y no con una casa porque la aplicación lo usa para el <c>catch</c> de última
    /// instancia —«la sesión se ha interrumpido por un error»—, que atrapa fallos de Atalaya, del
    /// disco y de la red además de los del proveedor. Formatearlos con el clasificador del
    /// dialecto de un SDK era decir de quién era el fallo sin haberlo comprobado (N-2).
    /// </para>
    /// </summary>
    public static string RawFailure(Exception? ex)
    {
        if (ex is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0)
            {
                sb.Append(" ← ");
            }

            sb.Append(e.GetType().Name).Append(": ").Append(e.Message?.Trim());
        }

        return sb.ToString();
    }
}
