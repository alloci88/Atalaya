namespace Atalaya.App.Services;

/// <summary>
/// <b>Qué identidad de commit se PUBLICA</b> (PROV-2 §5, D-037).
/// <para>
/// <b>De dónde sale.</b> La medida PROV-1 encontró que <c>apps/{slug}/fixes/{ulid}.json</c> —que
/// se publica en el hub, o sea que va a git— guardaba el autor del commit del arreglo tal cual
/// venía del clon, correo incluido. En la máquina medida ese correo era una dirección personal de
/// retransmisión privada: un dato que el usuario no eligió publicar y que ya no se puede retirar
/// de un historial ajeno.
/// </para>
/// <para>
/// <b>Qué NO es esto.</b> No es una censura de lo que se ve: la línea del hash sigue enseñando la
/// identidad de git del clon tal cual (D-1035), porque ésa puede ser un marcador y quien juzga es
/// el usuario. Lo que se sanea es únicamente lo que se ESCRIBE, y solo al escribirlo.
/// </para>
/// <para>
/// <b>La regla</b>, en orden: sin correo se escribe el nombre y ya está; un <c>noreply</c> de
/// GitHub —o el correo público de la cuenta conectada— es público por construcción y pasa tal
/// cual; cualquier otro se sustituye por el de D-037 de la cuenta
/// (<see cref="GitHubAccount.CommitEmail"/>) conservando el nombre del commit; y sin cuenta
/// conectada no hay <c>noreply</c> que construir, así que se escribe <b>solo el nombre</b>. Un
/// correo privado no sale al hub por ningún camino.
/// </para>
/// </summary>
public static class HubCommitIdentity
{
    /// <summary>El dominio con el que GitHub sustituye el correo privado de una cuenta.</summary>
    private const string NoReplyDomain = "@users.noreply.github.com";

    /// <summary>
    /// El valor que se guarda en <c>fixes/{ulid}.json</c>, a partir del autor real del commit
    /// (<c>«Nombre &lt;correo&gt;»</c>) y de la cuenta conectada, que puede no haberla.
    /// </summary>
    /// <returns>Lo que hay que escribir, o <c>null</c> cuando no queda nada publicable.</returns>
    public static string? ForHub(string? commitAuthor, GitHubAccount? account)
    {
        if (string.IsNullOrWhiteSpace(commitAuthor))
        {
            return commitAuthor;
        }

        (string name, string? email) = Split(commitAuthor.Trim());

        // 1. Sin correo no hay nada que sanear.
        if (email is null)
        {
            return commitAuthor;
        }

        // 2. Ya es público: el noreply de GitHub, o el correo que la cuenta publica en su perfil.
        if (email.EndsWith(NoReplyDomain, StringComparison.OrdinalIgnoreCase)
            || (account is not null
                && !string.IsNullOrWhiteSpace(account.Email)
                && string.Equals(email, account.Email!.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return commitAuthor;
        }

        // 4. Sin cuenta no se puede construir el noreply. Se va el correo y se queda el nombre:
        //    quedarse sin autor pierde una traza, publicar el correo no se puede deshacer.
        if (account is null)
        {
            return name.Length == 0 ? null : name;
        }

        // 3. El nombre del commit, con el correo de D-037.
        return name.Length == 0
            ? $"<{account.CommitEmail}>"
            : $"{name} <{account.CommitEmail}>";
    }

    /// <summary>
    /// Parte <c>«Nombre &lt;correo&gt;»</c>. Un valor que sea solo un correo —sin los ángulos— se
    /// trata también como correo: el saneo no puede depender de que el formato venga bien puesto.
    /// </summary>
    private static (string Name, string? Email) Split(string text)
    {
        int open = text.LastIndexOf('<');
        if (open >= 0 && text.EndsWith(">", StringComparison.Ordinal))
        {
            string inner = text[(open + 1)..^1].Trim();
            return (text[..open].TrimEnd(), inner.Length == 0 ? null : inner);
        }

        return text.Contains('@', StringComparison.Ordinal)
            ? (string.Empty, text)
            : (text, null);
    }
}
