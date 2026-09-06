using System.Text;

namespace Atalaya.Agents;

/// <summary>
/// Los argumentos de UNA llamada a herramienta, contados <b>según el modelo los escribe</b>
/// (F30 §2).
/// <para>
/// <b>Vive en el vocabulario común y no en cada casa</b> porque la regla de contar es la misma para
/// las dos y no tiene nada de específico: lo que cambia es el evento por el que llegan los trozos
/// —<c>input_json_delta</c> en Claude Code, <c>AssistantToolCallDeltaEvent.InputDelta</c> en
/// Copilot—, no qué se cuenta. Dos copias acabarían contando distinto, y entonces la misma sesión
/// diría «3 hallazgos» con una casa y «4» con la otra.
/// </para>
/// <para>
/// <b>No se parsea el JSON.</b> Lo que llega está a medias por definición —un array que todavía se
/// está escribiendo no es JSON válido—, así que deserializarlo fallaría en cada trozo. Se cuenta lo
/// único que se puede contar con certeza: los valores de una clave que ya han CERRADO su comilla.
/// Un elemento a medio escribir no cuenta hasta que su título está entero, que es exactamente lo
/// que se quiere enseñar — un título cortado por la mitad no informa de nada.
/// </para>
/// <para>
/// <b>Y se cuenta SOBRE LA MARCHA, carácter a carácter</b> (F30 §2e). La primera versión guardaba
/// el JSON acumulado y pasaba una expresión regular por <b>todo</b> él en cada trozo: con ~2.700
/// trozos por llamada y ~10 KB de argumentos eso es un barrido cuadrático dentro del bucle que lee
/// la salida del CLI —medido: <b>19,5 ms de los 29 ms</b> que costaba consumir una llamada entera,
/// dos tercios del gasto del lector—. El lector de un proceso vivo no puede hacer nada caro en
/// línea: lo que tarde en volver a leer es tiempo que el CLI pasa bloqueado escribiendo. Ahora cada
/// carácter se mira UNA vez y no se guarda el acumulado.
/// </para>
/// </summary>
public sealed class ToolCallInput
{
    /// <summary>
    /// Qué clave marca «un elemento más», por herramienta. Es el campo que identifica al elemento
    /// para quien lo lee: el título de un hallazgo, el veredicto de una reconciliación, la ruta de
    /// una ubicación.
    /// </summary>
    private static readonly Dictionary<string, string> Marker = new(StringComparer.Ordinal)
    {
        ["submit_findings"] = "title",
        ["submit_finding"] = "title",
        ["report_verdicts"] = "verdict",
        ["add_locations"] = "path",
    };

    private readonly string? _marker;

    /// <summary>La cadena que se está leyendo ahora mismo, ya sin comillas ni escapes.</summary>
    private readonly StringBuilder _text = new();

    private bool _inString;
    private bool _escaped;

    /// <summary>Los dígitos de un <c>\uXXXX</c> que puede venir partido entre dos trozos.</summary>
    private int _unicodePending;
    private int _unicodeValue;

    /// <summary>La última cadena cerrada era la clave marcadora: falta ver sus dos puntos.</summary>
    private bool _sawKey;

    /// <summary>Ya han pasado los dos puntos: la cadena que cierre ahora es el valor.</summary>
    private bool _expectValue;

    public ToolCallInput(string tool)
    {
        Tool = tool;
        _marker = Marker.TryGetValue(tool, out string? key) ? key : null;
    }

    /// <summary>El nombre corto de la herramienta, ya sin el prefijo del servidor.</summary>
    public string Tool { get; }

    /// <summary>Elementos cuya clave ya ha cerrado.</summary>
    public int Items { get; private set; }

    /// <summary>El último que se completó, para poder enseñarlo.</summary>
    public string? Last { get; private set; }

    /// <summary>
    /// El nombre de la herramienta sin el prefijo del servidor MCP: de
    /// <c>mcp__atalaya__submit_findings</c> a <c>submit_findings</c>. Lo que se enseña es la
    /// herramienta, no por dónde llegó.
    /// </summary>
    public static string Short(string name)
    {
        int at = name.LastIndexOf("__", StringComparison.Ordinal);
        return at < 0 ? name : name[(at + 2)..];
    }

    /// <summary>
    /// Añade un trozo. Devuelve <c>true</c> <b>solo</b> si con él se ha completado un elemento
    /// nuevo: así el hilo se repinta cuando hay algo que decir y no con cada puñado de caracteres —
    /// que a 64 tokens por segundo serían decenas de repintados por segundo sin información nueva.
    /// </summary>
    public bool Append(string chunk)
    {
        if (chunk.Length == 0 || _marker is null)
        {
            return false;
        }

        bool completed = false;
        foreach (char c in chunk)
        {
            completed |= Feed(c);
        }

        return completed;
    }

    /// <summary>
    /// Un carácter. El autómata es mínimo a propósito: solo distingue dentro/fuera de cadena, sus
    /// escapes, y si la cadena que acaba de cerrar era la clave marcadora o su valor. Nada de esto
    /// necesita ver el JSON entero, que es justo lo que lo hace barato.
    /// </summary>
    private bool Feed(char c)
    {
        if (_inString)
        {
            if (_unicodePending > 0)
            {
                _unicodeValue = (_unicodeValue * 16) + Hex(c);
                if (--_unicodePending == 0)
                {
                    _text.Append((char)_unicodeValue);
                }

                return false;
            }

            if (_escaped)
            {
                _escaped = false;
                switch (c)
                {
                    case 'u':
                        _unicodePending = 4;
                        _unicodeValue = 0;
                        break;
                    case 'n': _text.Append('\n'); break;
                    case 'r': _text.Append('\r'); break;
                    case 't': _text.Append('\t'); break;
                    case 'b': _text.Append('\b'); break;
                    case 'f': _text.Append('\f'); break;
                    default: _text.Append(c); break;
                }

                return false;
            }

            switch (c)
            {
                case '\\':
                    _escaped = true;
                    return false;
                case '"':
                    _inString = false;
                    return Closed();
                default:
                    _text.Append(c);
                    return false;
            }
        }

        switch (c)
        {
            case '"':
                _inString = true;
                _text.Clear();
                return false;

            // Los dos puntos son lo único que convierte una clave leída en «lo siguiente es su
            // valor». Los espacios entre la clave y ellos no cuentan, que es lo que un modelo
            // escribiendo JSON con sangría produce todo el rato.
            case ':' when _sawKey:
                _sawKey = false;
                _expectValue = true;
                return false;
            case ' ' or '\t' or '\r' or '\n':
                return false;
            default:
                _sawKey = false;
                _expectValue = false;
                return false;
        }
    }

    /// <summary>Una cadena acaba de cerrar: o es la clave que marca, o es su valor, o no es nada.</summary>
    private bool Closed()
    {
        if (_expectValue)
        {
            _expectValue = false;
            Items++;
            Last = _text.ToString();
            return true;
        }

        _sawKey = _text.Length == _marker!.Length && _text.ToString() == _marker;
        return false;
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}
