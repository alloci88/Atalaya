using System.Text.RegularExpressions;

namespace Atalaya.Agents;

/// <summary>
/// La puerta que se cierra por el otro lado (BUGFIX-LECTURA): un <c>ask_user</c> cuyas opciones le
/// piden al usuario que PEGUE código no llega a pintarse.
/// <para>
/// <b>Por qué hace falta además de la herramienta.</b> Que <c>read_file</c> sepa leer por rango
/// arregla la causa; esto arregla la consecuencia. Un agente que se convence de que un fichero no
/// se puede leer construye la tarjeta igual, y esa tarjeta convierte al usuario en la herramienta
/// de lectura —«Pégame en el chat las líneas 185 al final (Recomendado)»— con las otras dos
/// opciones peores todavía: arreglar a ciegas, o cancelar. La decisión se le devuelve al agente
/// con las mismas palabras que un permiso denegado: no es un error que reintentar, es un no.
/// </para>
/// <para>
/// <b>La regla es deliberadamente simple, y falla hacia dejar pasar.</b> Un verbo de pegar y un
/// sustantivo de código en la misma pregunta. Nada de intentar entender la frase: una tarjeta de
/// más le cuesta al usuario un clic, y una tarjeta de menos deja al agente esperando una respuesta
/// que nadie le va a dar. Por eso lleva UNA excepción, medida y no imaginada: en los informes del
/// hub, «copy-paste» y «copia-pega» aparecen nombrando el olor de código —«los 6 bloques están
/// copiados… se elimina el copy-paste»—, que no es pedirle nada a nadie.
/// </para>
/// </summary>
public static class FixAskGuard
{
    /// <summary>
    /// Lo que se le devuelve al agente en lugar de la respuesta del usuario. Le dice el NO y la
    /// salida en la misma frase: sin la salida, el agente repite la pregunta con otras palabras.
    /// </summary>
    public const string Refusal =
        "No: léelo tú con read_file(path, startLine, endLine). El usuario NO es tu herramienta de "
        + "lectura. La respuesta de read_file te dice cuántas líneas tiene el fichero y cuáles te "
        + "ha devuelto; pide el resto por rango, tantas veces como haga falta. No vuelvas a pedir "
        + "que te peguen código: replantea con lo que puedes leer tú.";

    /// <summary>Pegar, en las formas en que un agente se lo pide a una persona.</summary>
    private static readonly Regex Paste = new(
        @"\b(peg[aáue]\w*|paste[dr]?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Qué es lo que se pide pegar. Sin esto, «pega el enlace» sería un falso positivo.</summary>
    private static readonly Regex Code = new(
        @"\b(c[oó]digo|l[ií]neas?|fichero|archivo|m[eé]todo|funci[oó]n|contenido)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>El olor de código, que se NOMBRA con esas letras y no le pide nada al usuario.</summary>
    private static readonly Regex Smell = new(
        @"cop(y|ia)\s*[-&y]?\s*(paste|pega)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// ¿Esta pregunta le pide al usuario que pegue código? Mira la pregunta y CADA opción: la
    /// tarjeta del defecto tenía la pregunta neutra y el «pégame las líneas» en la opción
    /// recomendada.
    /// </summary>
    public static bool AsksTheUserToPasteCode(string? question, IEnumerable<string>? choices = null)
    {
        if (Asks(question))
        {
            return true;
        }

        foreach (string choice in choices ?? Enumerable.Empty<string>())
        {
            if (Asks(choice))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Asks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // El olor de código se retira ANTES de mirar: «se elimina el copy-paste de las 6 ramas»
        // lleva el verbo y el sustantivo, y no es una petición.
        string clean = Smell.Replace(text!, " ");
        return Paste.IsMatch(clean) && Code.IsMatch(clean);
    }
}
