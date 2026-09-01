namespace Atalaya.Agents;

/// <summary>
/// Los NOMBRES y las DESCRIPCIONES de las herramientas del arreglo asistido, en un solo sitio
/// (F16).
/// <para>
/// <b>Por qué constantes y no dos catálogos.</b> Con dos motores detrás del mismo contrato, la
/// superficie que ve el agente es lo único que hace comparables a las dos casas: si una tool se
/// llamara distinto —o su descripción dijera algo distinto— el mismo encargo significaría dos
/// cosas, y una diferencia entre proveedores dejaría de poder atribuirse al modelo. En F14 esto se
/// resolvió copiando el texto a mano y confiando en que nadie lo tocara solo en un lado; una
/// constante compartida lo convierte en imposible en vez de en improbable.
/// </para>
/// <para>
/// <b>Las cinco, y ni una más.</b> Cuatro son el toolbox (<see cref="ReadFile"/>,
/// <see cref="ApplyEdit"/>, <see cref="RunBuildAndTests"/>, <see cref="FixDone"/>) y la quinta es
/// la pregunta al usuario. En Copilot esa quinta la pone el RUNTIME (la tool <c>ask_user</c>, que
/// desemboca en <c>OnUserInputRequest</c>); en Claude Code el runtime se queda sin herramientas
/// propias a propósito, así que la pone Atalaya por MCP — con el mismo nombre, los mismos
/// argumentos y la misma tarjeta en la conversación. Lo que cambia es quién la transporta.
/// </para>
/// </summary>
public static class FixToolText
{
    public const string ReadFile = "read_file";

    public const string ReadFileDescription =
        "Lee un fichero del clon (ruta relativa a la raíz del repositorio). Tienes un "
        + "PRESUPUESTO de lecturas y la respuesta te dice cuántas te quedan: no explores, "
        + "lee lo que necesites para arreglar.";

    public const string ApplyEdit = "apply_edit";

    public const string ApplyEditDescription =
        "La ÚNICA forma de modificar código. path es relativo al clon; reason explica en una "
        + "frase por qué tocas ESE fichero; edits es un array de {oldText, newText, "
        + "replaceAll}. oldText debe aparecer EXACTAMENTE una vez (o marca replaceAll). "
        + "Sobre ficheros del hallazgo y sus tests se aplica directamente; sobre cualquier "
        + "otro, la aplicación le pedirá permiso al usuario y puede denegarlo.";

    public const string RunBuildAndTests = "run_build_and_tests";

    public const string RunBuildAndTestsDescription =
        "Pide a la aplicación que compile y ejecute los tests del clon y te devuelva un "
        + "resumen. Tú no tienes shell: esto es lo más parecido, y tarda, así que úsalo "
        + "cuando el cambio esté completo, no después de cada edición.";

    public const string FixDone = "fix_done";

    public const string FixDoneDescription =
        "Cierra el arreglo. summary: qué cambiaste y por qué, con los ficheros tocados. "
        + "commitTitle: ≤72 caracteres, imperativo, referenciando el identificador del "
        + "hallazgo. commitDescription: el qué y el porqué, incluyendo los llamadores "
        + "adaptados si los hubo. risks: lo que queda pendiente de revisión humana, o vacío.";

    /// <summary>
    /// La pregunta al usuario. El nombre es el de la tool del runtime de Copilot, y se conserva
    /// aunque aquí la sirva Atalaya: el encargo la nombra con estas letras.
    /// </summary>
    public const string AskUser = "ask_user";

    public const string AskUserDescription =
        "Pregúntale al usuario. Úsala cuando la decisión sea suya y no tuya —cambiar un contrato "
        + "observable, elegir entre dos arreglos con consecuencias distintas—: presenta las "
        + "opciones con su consecuencia concreta. question es la pregunta; choices son las "
        + "opciones cerradas (vacío = respuesta libre); allowFreeform permite además escribir "
        + "otra cosa. Devuelve {answer}. Bloquea hasta que el usuario conteste.";

    /// <summary>Las cinco, en orden, para quien tenga que afirmar sobre la lista entera.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        ReadFile, ApplyEdit, RunBuildAndTests, FixDone, AskUser,
    };
}
