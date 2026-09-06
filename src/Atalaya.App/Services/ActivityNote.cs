using Atalaya.Agents;

namespace Atalaya.App.Services;

/// <summary>De qué habla una línea del hilo de actividad (F30 §1).</summary>
public enum ActivityNoteKind
{
    /// <summary>Una herramienta que el auditor acaba de invocar.</summary>
    Tool,

    /// <summary>Un hito de la propia Atalaya: un corte, una reanudación fallida.</summary>
    Milestone,

    /// <summary>
    /// <b>Una herramienta que el modelo está ESCRIBIENDO</b> (F30 §2): todavía no se ha ejecutado.
    /// <para>
    /// Se distingue de <see cref="Tool"/> —que es la herramienta ya ejecutada— porque la línea es
    /// la misma y se va reescribiendo: «Reportando hallazgos…» y luego «Recibiendo hallazgos · 3 ·
    /// Credenciales embebidas…», hasta que la ejecución la sustituye por la definitiva. Es el tramo
    /// donde D-1014 midió el minuto: reportar once hallazgos son ~2.700 tokens de escritura, unos
    /// 42 s en los que hasta ahora no llegaba nada.
    /// </para>
    /// </summary>
    ToolWriting,

    /// <summary>
    /// <b>La entrega al modelo</b> (F30 §1b): el turno acaba de salir y a partir de aquí lo que
    /// pasa —o no pasa— es del modelo.
    /// <para>
    /// Tiene clase propia y no es un <see cref="Milestone"/> más porque el pie la usa para
    /// <b>anclar la espera</b>: «esperando al modelo» empieza a contar en el envío, no en el último
    /// evento, que es lo que hace que se lea como «Atalaya ya terminó lo suyo» en vez de como un
    /// silencio sin dueño.
    /// </para>
    /// </summary>
    Handover,
}

/// <summary>
/// Algo que acaba de pasar en el barrido, situado en su unidad y su pasada (F30 §1).
/// <para>
/// <b>El texto viaja CRUDO</b> —<c>submit_findings · items=5</c>—, tal y como
/// <c>SessionToolbox.ToolCallLog</c> lo apunta desde F3, y se traduce a castellano en la capa que
/// lo pinta. No es un descuido: ese mismo texto es el que va a <c>session.Notes</c> y de ahí al
/// anexo del informe, y tenerlo escrito dos veces —una para el registro y otra para la pantalla—
/// es la forma segura de que un día digan cosas distintas.
/// </para>
/// </summary>
/// <param name="Unit">La unidad que se está auditando; vacía si el hito no es de ninguna.</param>
/// <param name="Pass">La pasada en curso; 0 si todavía no ha empezado ninguna.</param>
public sealed record ActivityNote(string Unit, int Pass, ActivityNoteKind Kind, string Text);

/// <summary>
/// Cómo se lee una llamada a herramienta en el hilo de actividad (F30 §1).
/// <para>
/// <b>Traduce, no interpreta.</b> Lo que llega es la traza de diagnóstico que la toolbox ya
/// escribía —<c>report_verdicts · items=4</c>—, pensada para leerla en el anexo de un informe con
/// el manual al lado. En la pantalla se lee mientras trabajas, así que se dice en castellano y con
/// el número delante, que es lo que se busca de un vistazo. Lo que no reconoce lo devuelve tal
/// cual: una herramienta nueva tiene que salir en el hilo aunque nadie le haya escrito la frase
/// todavía — verla en crudo es infinitamente mejor que no verla.
/// </para>
/// </summary>
public static class ActivityWording
{
    /// <summary>
    /// El icono con el que se abre la línea.
    /// <para>
    /// <b>UNA FAMILIA PROPIA, y no los iconos de la narración</b>. La tentación era reutilizar el
    /// <c>＋</c> de «hallazgo nuevo» y el <c>⚖</c> de «disputado», y está mal por dos motivos. El
    /// de fondo: una llamada a <c>submit_findings</c> con cinco elementos es UN gesto del auditor,
    /// no cinco hallazgos —los cinco hallazgos ya tienen su línea cada uno—, así que darle el mismo
    /// icono haría contar dos veces lo mismo. Y el de forma: <c>LiveNarrationTests</c> cuenta los
    /// <c>＋</c> del hilo y exige que cuadren con los contadores de la sesión, y saltó a la primera
    /// —que es exactamente para lo que está—. El hilo dice «esto lo hizo la herramienta» con un
    /// icono suyo; la lectura de fichero se queda con el ojo del arreglo asistido, que ya significa
    /// eso mismo en la otra pantalla.
    /// </para>
    /// </summary>
    public static string GlyphFor(string entry)
        => Tool(entry) == "read_signatures" ? "👁" : "⚒";

    /// <summary>
    /// <b>De qué se está esperando</b> (F30 §1c): lo que el pie pone detrás de «esperando al
    /// modelo». Vacío cuando no hay nada útil que decir — mejor la frase corta que un relleno.
    /// <para>
    /// Existe porque el tramo importa. Un silencio tras <c>submit_findings</c> es el modelo
    /// escribiendo los argumentos del siguiente paso, y son <b>246 tokens por hallazgo</b> a
    /// <b>64 tokens por segundo</b> (medidos los dos sobre el hub real): reportar once son unos
    /// <b>42 s</b> en los que no puede llegar nada. Decir tras qué se espera convierte ese minuto
    /// en algo que se entiende.
    /// </para>
    /// </summary>
    public static string After(ActivityNote note)
    {
        if (note.Kind == ActivityNoteKind.Handover)
        {
            return note.Pass > 0 ? $"tras enviar la pasada {note.Pass}" : "tras enviar el turno";
        }

        if (note.Kind != ActivityNoteKind.Tool)
        {
            return string.Empty;
        }

        string entry = note.Text;
        return Tool(entry) switch
        {
            "submit_findings" => "tras " + Counted(entry, "reportar 1 hallazgo", "reportar", plural: true),
            "submit_finding" => "tras reportar 1 hallazgo",
            "report_verdicts" => "tras " + Counted(entry, "dar 1 veredicto", "dar", plural: true, noun: "veredictos"),
            "add_locations" => "tras añadir ubicaciones",
            "unit_done" => "tras cerrar la unidad",
            "read_signatures" => "tras leer un fichero",
            _ => string.Empty,
        };
    }

    /// <summary>«reportar 11 hallazgos» / «reportar 1 hallazgo», con el número delante del nombre.</summary>
    private static string Counted(string entry, string singular, string verb, bool plural, string noun = "hallazgos")
    {
        int n = Number(entry, "items");
        return n == 1 ? singular : $"{verb} {n} {noun}";
    }

    /// <summary>
    /// Cómo se lee una herramienta que el modelo TODAVÍA está escribiendo (F30 §2).
    /// <para>
    /// <b>Sin denominador, y no es un olvido.</b> Lo que llega es un array que se está escribiendo:
    /// cuántos elementos va a tener no se sabe hasta que cierra. Se dice lo que hay —«3
    /// hallazgos»—, nunca «3 de 11», porque el once habría que inventarlo y esta fase tiene
    /// prohibido inventar progreso. El número definitivo lo da la línea de la ejecución.
    /// </para>
    /// </summary>
    public static string Writing(ToolStream tool)
    {
        string verb = tool.Tool switch
        {
            "submit_findings" or "submit_finding" => "hallazgos",
            "report_verdicts" => "veredictos",
            "add_locations" => "ubicaciones",
            "read_signatures" => "la lectura",
            _ => tool.Tool,
        };

        if (tool.Phase == ToolStreamPhase.Started || tool.Items == 0)
        {
            return tool.Tool switch
            {
                "submit_findings" or "submit_finding" => "Reportando hallazgos…",
                "report_verdicts" => "Juzgando los hallazgos existentes…",
                "add_locations" => "Añadiendo ubicaciones…",
                "read_signatures" => "Leyendo el fichero…",
                _ => $"Llamando a {tool.Tool}…",
            };
        }

        string tail = tool.Last is { Length: > 0 } last ? $" · {Trim(last)}" : string.Empty;
        return $"Recibiendo {verb} · {tool.Items}{tail}";
    }

    /// <summary>Un título largo no puede empujar la fila: se corta con puntos suspensivos.</summary>
    private static string Trim(string text)
        => text.Length <= 60 ? text : text[..59].TrimEnd() + "…";

    /// <summary>La frase que se lee. Ver la nota de la clase sobre lo que no reconoce.</summary>
    public static string Describe(string entry)
    {
        string tool = Tool(entry);
        return tool switch
        {
            "submit_findings" => Counted(entry, "hallazgo nuevo", "hallazgos nuevos"),
            "submit_finding" => "1 hallazgo nuevo" + Quoted(entry, "title"),
            "report_verdicts" => Counted(entry, "veredicto sobre un hallazgo existente",
                "veredictos sobre hallazgos existentes"),
            "add_locations" => Located(entry),
            "unit_done" => "Unidad cerrada",
            "read_signatures" => "Ha leído " + (Field(entry, "path") ?? "un fichero"),
            _ => entry,
        };
    }

    /// <summary>El nombre de la herramienta: lo que va antes del primer separador.</summary>
    private static string Tool(string entry)
    {
        int at = entry.IndexOf(" · ", StringComparison.Ordinal);
        return (at < 0 ? entry : entry[..at]).Trim();
    }

    /// <summary>«5 hallazgos nuevos» / «1 hallazgo nuevo», concordando como el resto de la casa.</summary>
    private static string Counted(string entry, string singular, string plural)
    {
        int n = Number(entry, "items");
        return $"{n} {(n == 1 ? singular : plural)}";
    }

    /// <summary>«Añade 2 ubicaciones a HAL-0412».</summary>
    private static string Located(string entry)
    {
        int n = Number(entry, "locs");
        string to = Field(entry, "id") is { Length: > 0 } id ? $" a {id}" : string.Empty;
        return $"Añade {n} {(n == 1 ? "ubicación" : "ubicaciones")}{to}";
    }

    private static string Quoted(string entry, string key)
        => Field(entry, key) is { Length: > 0 } value ? $": {value}" : string.Empty;

    /// <summary>El valor de <c>clave='…'</c> dentro de la traza. Null si no está.</summary>
    private static string? Field(string entry, string key)
    {
        int at = entry.IndexOf(key + "='", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        int from = at + key.Length + 2;
        int to = entry.IndexOf('\'', from);
        return to < 0 ? entry[from..] : entry[from..to];
    }

    /// <summary>El valor de <c>clave=N</c>. Cero si no está o no es un número.</summary>
    private static int Number(string entry, string key)
    {
        int at = entry.IndexOf(key + "=", StringComparison.Ordinal);
        if (at < 0)
        {
            return 0;
        }

        int from = at + key.Length + 1;
        int to = from;
        while (to < entry.Length && char.IsDigit(entry[to]))
        {
            to++;
        }

        return int.TryParse(entry[from..to], out int n) ? n : 0;
    }
}
