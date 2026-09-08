using Atalaya.Storage.Sync;

namespace Atalaya.Tests;

/// <summary>
/// <b>BUGFIX-CI-2 — un test de publicación afirma CON EL MOTIVO.</b>
/// <para>
/// La regla que sirve esta clase: <b>un <c>true</c>/<c>false</c> a secas no se acepta</b> en un test
/// que publica al hub. Dos tests caían a ratos en Actions —y solo en Actions— con
/// <c>CommitAndPush</c> devolviendo <c>false</c> o la salud en rojo, y el parte que volvía decía
/// «Expected … to be True», que no es un diagnóstico: es la constatación de que no se sabe nada. En
/// esta máquina se depura con el registro delante; del runner solo vuelve el <c>.trx</c> que el
/// workflow sube, así que <b>lo que no esté en el <c>because</c> no existe</b>.
/// </para>
/// <para>
/// Las llaves se cambian por paréntesis: el <c>because</c> de FluentAssertions viaja por una
/// plantilla de formato, y un <c>{0}</c> que viniera dentro del mensaje de git se comería el
/// motivo entero. Ningún mensaje de los que salen por aquí las lleva, y perderlas cuesta menos que
/// perder la frase.
/// </para>
/// </summary>
internal static class HubDiagnostics
{
    /// <summary>
    /// Lo último que el sync tuvo que decir, listo para un <c>because</c>. Primero el diario de
    /// intentos —que es el que existe justo cuando la publicación se agotó sin motivo visible— y,
    /// si no lo hay, la frase que se le enseñaría al usuario.
    /// </summary>
    public static string Why(this HubSyncService sync)
        => Because(sync.LastPublishFailure ?? sync.LastError);

    /// <summary>El mismo motivo, para quien ya tiene la cadena y no el servicio.</summary>
    public static string Because(string? lastError)
    {
        string motivo = string.IsNullOrWhiteSpace(lastError)
            ? "el sync no registró ningún motivo"
            : lastError!;
        return motivo.Replace('{', '(').Replace('}', ')');
    }
}
