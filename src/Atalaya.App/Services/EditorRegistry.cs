using System.Text;

namespace Atalaya.App.Services;

/// <summary>Qué clase de cosa es un editor del registro, porque no todos se lanzan igual.</summary>
public enum EditorKind
{
    /// <summary>Un ejecutable que hay que encontrar en la máquina y al que se le pasan argumentos.</summary>
    Program,

    /// <summary>El manejador del sistema: se abre el fichero y decide Windows. El último recurso de D-208.</summary>
    System,

    /// <summary>El comando que escribe el usuario, con <c>{file}</c>, <c>{line}</c> y <c>{col}</c>.</summary>
    Custom,
}

/// <summary>
/// Cómo se le dice a un editor «y ponte en la línea N» — <b>o la declaración explícita de que no se
/// le puede decir</b>.
/// <para>
/// No hay tercera opción, y ese es el punto: el defecto que abrió esta tanda es que Visual Studio
/// caía en el <c>else</c> de un <c>switch</c> de dos casos y se quedaba sin línea <b>sin decirlo</b>.
/// Un editor que no declara ni una cosa ni la otra no entra en el registro, y hay un test que lo
/// comprueba.
/// </para>
/// </summary>
public sealed class EditorLineSyntax
{
    private EditorLineSyntax(string? template, string? whyNot, bool byUser)
    {
        Template = template;
        WhyNot = whyNot;
        ByUser = byUser;
    }

    /// <summary>Los argumentos que llevan la línea, con marcadores; <c>null</c> si no admite línea.</summary>
    public string? Template { get; }

    /// <summary>Por qué no admite línea, en una frase para el usuario; <c>null</c> si sí la admite.</summary>
    public string? WhyNot { get; }

    /// <summary>
    /// La sintaxis la escribe el usuario («Otro»): la declara su comando, y se comprueba al
    /// construirlo. Es una declaración explícita, no un hueco.
    /// </summary>
    public bool ByUser { get; }

    public bool Supported => Template is not null;

    /// <summary>Admite línea, y así se escribe.</summary>
    public static EditorLineSyntax Args(string template) => new(template, null, byUser: false);

    /// <summary>No admite línea, y este es el motivo que se le enseña al usuario.</summary>
    public static EditorLineSyntax None(string whyNot) => new(null, whyNot, byUser: false);

    /// <summary>La declara el usuario en su comando personalizado.</summary>
    public static EditorLineSyntax Declared() => new(null, null, byUser: true);
}

/// <summary>
/// Un editor del registro: <b>datos</b>, no un caso de un <c>switch</c>.
/// </summary>
/// <param name="Id">Lo que se guarda en <c>settings.json</c>. No cambia nunca.</param>
/// <param name="Name">El nombre visible, el del desplegable y el del toast.</param>
/// <param name="Executables">Los ejecutables candidatos, en orden de preferencia.</param>
/// <param name="Folders">
/// Dónde buscarlos además del PATH y del registro de Windows. Admiten variables de entorno
/// (<c>%ProgramFiles%</c>) y un <c>*</c> por segmento de carpeta, que es lo que hace falta para
/// Visual Studio (año × edición) y para los JetBrains (una carpeta por versión).
/// </param>
/// <param name="Line">La sintaxis de línea, o la declaración de que no la hay.</param>
/// <param name="ArgsWithoutLine">Los argumentos cuando no hay línea que dar.</param>
public sealed record EditorDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Executables,
    IReadOnlyList<string> Folders,
    EditorLineSyntax Line,
    string ArgsWithoutLine,
    EditorKind Kind = EditorKind.Program);

/// <summary>
/// El comando que se va a lanzar, ya construido: <b>lo que se ejecuta y lo que se le cuenta al
/// usuario son el mismo dato</b>, para que el toast y el botón «Probar» no puedan mentir sobre lo
/// que de verdad se lanzó.
/// </summary>
/// <param name="Error">Si no se pudo construir, el motivo. Con error no se lanza nada.</param>
public sealed record EditorCommand(
    string FileName,
    string Arguments,
    bool UseShell,
    bool CarriesLine,
    string? NoLineReason,
    string? Error)
{
    public bool Ok => Error is null;

    /// <summary>El comando en una línea, tal cual, para enseñarlo.</summary>
    public string Display => Arguments.Length == 0
        ? Quote(FileName)
        : Quote(FileName) + " " + Arguments;

    private static string Quote(string s) => s.Contains(' ') && !s.StartsWith('"') ? "\"" + s + "\"" : s;

    internal static EditorCommand Failed(string reason) => new(string.Empty, string.Empty, false, false, null, reason);
}

/// <summary>
/// <b>Los editores, como datos.</b> Antes eran dos casos de un <c>if</c> dentro del lanzador —Visual
/// Studio y VS Code— y cualquier otro editor era imposible sin tocar código; peor, el caso por
/// defecto se tragaba el ajuste sin decirlo.
/// <para>
/// Aquí cada editor declara su nombre, sus ejecutables, dónde buscarlos y <b>su sintaxis de línea o
/// que no tiene</b>. Añadir uno es añadir una fila. Y para todo lo que no esté en la tabla —que es
/// lo que evita tener que mantenerla para siempre— está <see cref="CustomId"/>: el comando lo
/// escribe el usuario.
/// </para>
/// <para>
/// <b>Lo que NO se inventa.</b> Ninguna sintaxis de esta tabla se ha adivinado: las que no se han
/// podido comprobar se declaran «sin línea» (Visual Studio) o se quedan fuera (Eclipse), que para
/// eso está «Otro».
/// </para>
/// </summary>
public static class EditorRegistry
{
    public const string VisualStudioId = "vs";
    public const string SystemId = "system";
    public const string CustomId = "custom";

    /// <summary>Los marcadores del comando personalizado, en un sitio: la ayuda de Ajustes los cita.</summary>
    public const string FilePlaceholder = "{file}";
    public const string LinePlaceholder = "{line}";
    public const string ColumnPlaceholder = "{col}";

    /// <summary>Un ejemplo real para la ayuda del campo «Otro».</summary>
    public const string CustomExample = "\"C:\\Program Files\\Notepad++\\notepad++.exe\" -n{line} \"{file}\"";

    private static readonly EditorDefinition[] Definitions =
    [
        new(VisualStudioId, "Visual Studio",
            ["devenv.exe"],
            [
                @"%ProgramFiles%\Microsoft Visual Studio\*\*\Common7\IDE\devenv.exe",
                @"%ProgramFiles(x86)%\Microsoft Visual Studio\*\*\Common7\IDE\devenv.exe",
            ],
            // `devenv /Edit fichero` abre el fichero y no acepta ningún número de línea: la
            // documentación del conmutador no lo tiene y no se ha encontrado ninguna variante que
            // lo lleve. Ir a la línea con la instancia en marcha exige la automatización DTE
            // (`EnvDTE.TextSelection.GotoLine`) por COM contra el `ROT`, que es otra cosa —y no se
            // ha podido medir aquí, así que NO se finge: se abre el fichero y se dice (§3).
            EditorLineSyntax.None("no lo permite desde la línea de comandos"),
            "/Edit \"{file}\""),

        new("vscode", "VS Code",
            ["code.cmd", "code.exe"],
            [
                @"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd",
                @"%ProgramFiles%\Microsoft VS Code\bin\code.cmd",
                @"%ProgramFiles(x86)%\Microsoft VS Code\bin\code.cmd",
            ],
            EditorLineSyntax.Args("-g \"{file}:{line}:{col}\""),
            "\"{file}\""),

        new("notepadpp", "Notepad++",
            ["notepad++.exe"],
            [
                @"%ProgramFiles%\Notepad++\notepad++.exe",
                @"%ProgramFiles(x86)%\Notepad++\notepad++.exe",
                @"%LOCALAPPDATA%\Programs\Notepad++\notepad++.exe",
            ],
            EditorLineSyntax.Args("-n{line} \"{file}\""),
            "\"{file}\""),

        // Rider, Android Studio e IntelliJ son la misma herramienta con tres nombres: el mismo
        // lanzador de JetBrains, el mismo `--line`, y la misma costumbre de instalarse en una
        // carpeta por versión bajo Toolbox o Programs.
        new("rider", "JetBrains Rider",
            ["rider64.exe", "rider.exe"],
            [
                @"%ProgramFiles%\JetBrains\*\bin\rider64.exe",
                @"%LOCALAPPDATA%\Programs\Rider\bin\rider64.exe",
                @"%LOCALAPPDATA%\JetBrains\Toolbox\apps\*\*\*\bin\rider64.exe",
            ],
            EditorLineSyntax.Args("--line {line} \"{file}\""),
            "\"{file}\""),

        new("studio", "Android Studio",
            ["studio64.exe", "studio.exe"],
            [
                @"%ProgramFiles%\Android\Android Studio\bin\studio64.exe",
                @"%LOCALAPPDATA%\Programs\Android Studio\bin\studio64.exe",
                @"%LOCALAPPDATA%\JetBrains\Toolbox\apps\*\*\*\bin\studio64.exe",
            ],
            EditorLineSyntax.Args("--line {line} \"{file}\""),
            "\"{file}\""),

        new("idea", "IntelliJ IDEA",
            ["idea64.exe", "idea.exe"],
            [
                @"%ProgramFiles%\JetBrains\*\bin\idea64.exe",
                @"%LOCALAPPDATA%\Programs\IntelliJ IDEA\bin\idea64.exe",
                @"%LOCALAPPDATA%\JetBrains\Toolbox\apps\*\*\*\bin\idea64.exe",
            ],
            EditorLineSyntax.Args("--line {line} \"{file}\""),
            "\"{file}\""),

        new("netbeans", "NetBeans",
            ["netbeans.exe", "netbeans64.exe"],
            [
                @"%ProgramFiles%\NetBeans*\bin\netbeans64.exe",
                @"%ProgramFiles%\NetBeans*\bin\netbeans.exe",
            ],
            EditorLineSyntax.Args("--open \"{file}:{line}\""),
            "\"{file}\""),

        new("sublime", "Sublime Text",
            ["subl.exe", "sublime_text.exe"],
            [
                @"%ProgramFiles%\Sublime Text\subl.exe",
                @"%ProgramFiles%\Sublime Text 3\subl.exe",
                @"%ProgramFiles(x86)%\Sublime Text\subl.exe",
            ],
            EditorLineSyntax.Args("\"{file}:{line}:{col}\""),
            "\"{file}\""),

        new(SystemId, "Manejador del sistema",
            [],
            [],
            // Abrir por asociación es entregarle el fichero a Windows: no hay ningún sitio donde
            // meter un número de línea, lo abra quien lo abra.
            EditorLineSyntax.None("el sistema abre el fichero con la aplicación asociada y esa vía no lleva línea"),
            string.Empty,
            EditorKind.System),

        new(CustomId, "Otro (comando personalizado)",
            [],
            [],
            // La sintaxis la declara el usuario en su comando. Si no trae {line}, se dice —igual
            // que con Visual Studio— en vez de callarlo.
            EditorLineSyntax.Declared(),
            string.Empty,
            EditorKind.Custom),
    ];

    /// <summary>Todos los editores conocidos, en el orden en el que se ofrecen.</summary>
    public static IReadOnlyList<EditorDefinition> All => Definitions;

    /// <summary>El editor con ese id, o <c>null</c> si el ajuste guarda uno que ya no existe.</summary>
    public static EditorDefinition? Find(string? id)
        => Definitions.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>El nombre visible del id guardado, o el propio id si no se reconoce.</summary>
    public static string NameOf(string? id) => Find(id)?.Name ?? (id is { Length: > 0 } ? id : "—");

    /// <summary>
    /// El comando para abrir <paramref name="file"/> en <paramref name="line"/> con este editor.
    /// <b>Función pura</b>: no toca disco ni lanza nada, así que se puede probar entera sin tener
    /// ningún editor instalado — el patrón que D-208 estrenó con el tope de tiempo.
    /// </summary>
    /// <param name="executable">
    /// Dónde se encontró el ejecutable. <c>null</c> para los que no lo necesitan (el manejador del
    /// sistema y el comando personalizado).
    /// </param>
    /// <param name="custom">El comando del usuario, solo para <see cref="EditorKind.Custom"/>.</param>
    public static EditorCommand Build(
        EditorDefinition editor, string? executable, string file, int line, int column = 1, string? custom = null)
    {
        if (editor.Kind == EditorKind.System)
        {
            return new EditorCommand(file, string.Empty, UseShell: true, CarriesLine: false, editor.Line.WhyNot, null);
        }

        if (editor.Kind == EditorKind.Custom)
        {
            return BuildCustom(custom, file, line, column);
        }

        if (string.IsNullOrWhiteSpace(executable))
        {
            return EditorCommand.Failed($"{editor.Name} no está en esta máquina");
        }

        bool withLine = line > 0 && editor.Line.Supported;
        string template = withLine ? editor.Line.Template! : editor.ArgsWithoutLine;

        return new EditorCommand(
            executable!,
            Fill(template, file, line, column),
            UseShell: false,
            CarriesLine: withLine,
            withLine ? null : editor.Line.WhyNot,
            null);
    }

    /// <summary>
    /// «Otro»: el comando entero lo escribe el usuario. Se le exige <b>una</b> cosa —que diga qué
    /// fichero abrir— y si no la cumple se falla <b>con motivo</b>, que es justo lo que no hacía el
    /// caso por defecto que abrió esta tanda.
    /// </summary>
    private static EditorCommand BuildCustom(string? custom, string file, int line, int column)
    {
        if (string.IsNullOrWhiteSpace(custom))
        {
            return EditorCommand.Failed(
                "«Otro» está elegido pero no has escrito ningún comando en Ajustes › Avanzado");
        }

        string command = custom!.Trim();
        if (!command.Contains(FilePlaceholder, StringComparison.Ordinal))
        {
            return EditorCommand.Failed(
                $"el comando personalizado no contiene {FilePlaceholder}, así que no dice qué fichero abrir");
        }

        (string exe, string args) = SplitCommand(command);
        bool carriesLine = line > 0 && command.Contains(LinePlaceholder, StringComparison.Ordinal);

        return new EditorCommand(
            Fill(exe, file, line, column),
            Fill(args, file, line, column),
            UseShell: false,
            carriesLine,
            carriesLine ? null : $"tu comando personalizado no incluye {LinePlaceholder}",
            null);
    }

    /// <summary>
    /// Parte «programa» de «argumentos». Con comillas manda la comilla —un ejecutable en
    /// <c>Program Files</c> las necesita—; sin comillas, el primer espacio.
    /// </summary>
    internal static (string Executable, string Arguments) SplitCommand(string command)
    {
        if (command.StartsWith('"'))
        {
            int close = command.IndexOf('"', 1);
            return close < 0
                ? (command.Trim('"'), string.Empty)
                : (command[1..close], command[(close + 1)..].TrimStart());
        }

        int space = command.IndexOf(' ');
        return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].TrimStart());
    }

    /// <summary>Sustituye los tres marcadores. Los tres, siempre: uno sin sustituir llegaría literal.</summary>
    internal static string Fill(string template, string file, int line, int column)
        => new StringBuilder(template)
            .Replace(FilePlaceholder, file)
            .Replace(LinePlaceholder, Math.Max(line, 1).ToString())
            .Replace(ColumnPlaceholder, Math.Max(column, 1).ToString())
            .ToString();
}
