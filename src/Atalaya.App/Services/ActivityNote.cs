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
    /// <b>La entrega al agente</b> (F30 §1b): el turno acaba de salir y a partir de aquí lo que
    /// pasa —o no pasa— es suyo.
    /// <para>
    /// Tiene clase propia y no es un <see cref="Milestone"/> más porque el pie la usa para
    /// <b>anclar la espera</b>: «esperando al agente» empieza a contar en el envío, no en el último
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
/// <param name="Waiting">
/// <b>Lo que el pie tiene que decir</b> mientras esto dure, sin el contador (F30 §2e). Vacío
/// cuando el hito no cambia lo que se está esperando, y entonces se conserva lo anterior.
/// <para>
/// Viaja con la nota y no se deduce del texto en la capa que pinta, por lo mismo que el texto
/// crudo viaja en <see cref="Text"/>: adivinar la frase del pie leyendo la de la pantalla sería
/// dos verdades escritas en dos sitios, y un día dirían cosas distintas.
/// </para>
/// </param>
public sealed record ActivityNote(
    string Unit, int Pass, ActivityNoteKind Kind, string Text, string Waiting = "");

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
    /// <summary>
    /// <b>Lo que viene después del texto</b> (F30 §2d): el modelo escribiendo el reporte.
    /// <para>
    /// Es una <b>inferencia</b>, y se dice como tal aquí para que nadie la confunda con una medida:
    /// nadie ha visto llegar esos tokens —Copilot no publica los argumentos de sus function tools
    /// (§2b)—. Lo que la sostiene es D-1014: la duración de una pasada es su salida de tokens
    /// (r = 0,985), y el tramo entre el último texto y la ejecución de la herramienta es
    /// exactamente donde caben los ~246 tokens por hallazgo que hay que escribir. Decirlo es más
    /// honrado que un «esperando» a secas, que se lee como un cuelgue.
    /// </para>
    /// </summary>
    /// <para>
    /// <b>F30 §2e — la frase es COMPLETA, no un sufijo.</b> Hasta aquí el pie escribía siempre
    /// «esperando al modelo» y esto añadía el tramo detrás, así que el tramo del reporte salía como
    /// «esperando al modelo escribiendo el reporte»: dos verbos peleándose por la misma frase. Lo
    /// que se está haciendo en ese tramo no es esperar, es recibir un reporte que se está
    /// escribiendo, y se dice así.
    /// </para>
    public const string AfterText = "escribiendo el reporte de hallazgos";

    /// <summary>La frase por defecto: hay turno en el aire y no se sabe decir nada más preciso.</summary>
    /// <remarks>
    /// <b>«al agente», no «al modelo»</b> (F30 §3). La voz de enfrente se llama Agente en toda la
    /// conversación —lo era ya en el arreglo asistido desde F16—, y el pie de la misma pantalla no
    /// puede llamarla de otra manera.
    /// </remarks>
    public const string Waiting = "esperando al agente";

    /// <summary>
    /// Lo que el hilo dice mientras el agente piensa (F30 §2e). Es una constante y no una cadena
    /// suelta porque de ella depende la clase de la entrada: el razonamiento tiene burbuja propia.
    /// </summary>
    public const string Reasoning = "Razonando…";

    /// <summary>
    /// La frase entera que el pie escribe mientras dura el silencio, sin el contador. Vacía cuando
    /// el evento no cambia lo que se está esperando —una muestra de consumo no lo cambia—, y
    /// entonces se conserva la anterior.
    /// </summary>
    public static string After(ActivityNote note)
    {
        if (note.Kind == ActivityNoteKind.Handover)
        {
            // §2e — y aquí ya no va el número de pasada. El pie lo dice dos segmentos antes
            // («Unidad 1 de 1 · pasada 2»), y repetirlo gastaba el sitio del único dato que este
            // segmento aporta, que es el tiempo.
            return Waiting;
        }

        if (note.Kind != ActivityNoteKind.Tool)
        {
            return string.Empty;
        }

        string entry = note.Text;
        string after = Tool(entry) switch
        {
            "submit_findings" => "tras " + Counted(entry, "reportar 1 hallazgo", "reportar", plural: true),
            "submit_finding" => "tras reportar 1 hallazgo",
            "report_verdicts" => "tras " + Counted(entry, "dar 1 veredicto", "dar", plural: true, noun: "veredictos"),
            "add_locations" => "tras añadir ubicaciones",
            "unit_done" => "tras cerrar la unidad",
            "read_signatures" => "tras leer un fichero",
            _ => string.Empty,
        };

        return after.Length == 0 ? Waiting : $"{Waiting} {after}";
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
        if (tool.Phase == ToolStreamPhase.Reasoning)
        {
            return Reasoning;
        }

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

    /// <summary>
    /// <b>Qué dice el pie mientras el modelo escribe esto</b> (F30 §2e). Es la mitad de la
    /// información del tramo largo: «razonando · 18 s» y «escribiendo el reporte de hallazgos ·
    /// 31 s» explican un silencio; «esperando al modelo» a secas se lee como un cuelgue.
    /// </summary>
    public static string WaitingFor(ToolStream tool)
    {
        if (tool.Phase == ToolStreamPhase.Reasoning)
        {
            return "razonando";
        }

        return tool.Tool switch
        {
            "submit_findings" or "submit_finding" => AfterText,
            "report_verdicts" => "escribiendo los veredictos",
            "add_locations" => "escribiendo las ubicaciones",
            "read_signatures" => "pidiendo un fichero",
            _ => $"escribiendo la llamada a {tool.Tool}",
        };
    }

    /// <summary>
    /// <b>De qué clase de evento es una nota</b> (F30 §3): con eso el hilo elige su plantilla, la
    /// misma que usa el arreglo asistido para lo mismo.
    /// <para>
    /// Vive aquí y no en el emisor por lo mismo que la frase: lo que viaja por el canal es la traza
    /// cruda, y la lectura —castellano, icono, forma— se hace una sola vez en la capa que pinta.
    /// </para>
    /// </summary>
    public static ConversationKind KindOf(ActivityNote note) => note.Kind switch
    {
        ActivityNoteKind.Handover => ConversationKind.Entrega,

        // El razonamiento no es una herramienta: no hay llamada, hay un agente pensando, y su
        // burbuja se lee en cursiva mientras dura.
        ActivityNoteKind.ToolWriting => note.Text == Reasoning
            ? ConversationKind.Razonamiento
            : ConversationKind.Herramienta,

        // `unit_done` cierra la unidad. Es una herramienta como las otras en el registro, pero en
        // el hilo es el final de un tramo y lleva su propio icono.
        ActivityNoteKind.Tool => Tool(note.Text) switch
        {
            "unit_done" => ConversationKind.Unidad,
            "report_verdicts" => ConversationKind.Veredicto,
            _ => ConversationKind.Herramienta,
        },

        _ => ConversationKind.Hito,
    };

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
