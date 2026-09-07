using System.Diagnostics;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// De dónde sale la línea que se le manda al editor (D-021).
/// <para>
/// Un hallazgo cuya unidad ha cambiado se abre en la línea <b>re-anclada</b> si la hay, y si no se
/// ancla, en la original <b>diciéndolo</b>. La distinción no es cosmética: mandar a alguien a la
/// línea 142 de un fichero que ya se movió es mandarlo a leer otra cosa creyendo que es la suya.
/// </para>
/// </summary>
public enum LineOrigin
{
    /// <summary>La línea guardada sigue siendo la buena. No hay nada que contar.</summary>
    Anclada,

    /// <summary>El código está en otra línea y se ha encontrado: se abre ahí y se dice de dónde venía.</summary>
    Reanclada,

    /// <summary>No se ha podido anclar: se abre en la original y se dice que la unidad cambió.</summary>
    SinAnclar,
}

/// <summary>Qué pasó al abrir, con el detalle que necesita el toast (§3) y el botón «Probar» (§2).</summary>
/// <param name="Command">El comando literal que se lanzó, o el que se habría lanzado si falló antes.</param>
/// <param name="Line">La línea a la que se mandó al editor (la re-anclada cuando la hay, D-021).</param>
/// <param name="OriginalLine">La que traía el hallazgo, para poder decir «antes 142».</param>
/// <param name="Failure">El motivo del fallo. <c>null</c> cuando abrió.</param>
public sealed record EditorOpenResult(
    bool Opened,
    string EditorName,
    string Command,
    int Line,
    bool LineDelivered,
    string? NoLineReason,
    string? Failure,
    LineOrigin Origin = LineOrigin.Anclada,
    int OriginalLine = 0)
{
    internal static EditorOpenResult Failed(string editorName, string reason, string command = "")
        => new(false, editorName, command, 0, false, null, reason);
}

/// <summary>
/// Abre la ubicación de un hallazgo en el editor que dice el ajuste.
/// <para>
/// <b>Un solo camino.</b> Todos los «Abrir en el editor» de la aplicación —la ficha del hallazgo y
/// el arreglo terminado— pasan por aquí, y aquí el ajuste se lee <b>en cada pulsación</b>: cambiarlo
/// surte efecto la próxima vez que abras código, sin reiniciar (BUGFIX-AJUSTES §3, R5).
/// </para>
/// <para>
/// <b>Y no se cae a otro editor en silencio.</b> Hasta R13, un <c>devenv</c> que no resolvía —que es
/// lo normal: Visual Studio no lo pone en el PATH— se tragaba la excepción y abría el fichero con el
/// manejador del sistema devolviendo <c>true</c>. El usuario elegía Visual Studio, se le abría otra
/// cosa, y nadie decía nada. Ahora el manejador del sistema es un editor que <b>se elige</b>, y un
/// editor que no está es un fallo con su motivo.
/// </para>
/// </summary>
public sealed class EditorLauncher
{
    private readonly SettingsService _settings;
    private readonly MachineConfigStore _machines;
    private readonly EditorDetector _detector;

    public EditorLauncher(SettingsService settings, MachineConfigStore machines, EditorDetector? detector = null)
    {
        _settings = settings;
        _machines = machines;
        _detector = detector ?? new EditorDetector();
    }

    /// <summary>
    /// Lo que se espera a que el editor arranque antes de darlo por fallido (F5.5 §6).
    /// <para>
    /// <c>Process.Start</c> parece instantáneo y no lo es: resolver <c>devenv</c> por el PATH,
    /// levantar el shim <c>code.cmd</c> o caer en el manejador del sistema puede bloquear el hilo
    /// varios segundos —y con una unidad de red desconectada, indefinidamente—. La ficha decía
    /// «Abriendo en el editor…» y se quedaba ahí para siempre porque nadie ponía un límite. Ahora
    /// lo hay: o abre, o falla, pero termina.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Abre la ubicación con un tope de tiempo. Devuelve <c>false</c> si el editor no arrancó
    /// dentro de <see cref="LaunchTimeout"/>: el arranque sigue su curso en segundo plano, pero la
    /// interfaz deja de esperarlo.
    /// </summary>
    public Task<EditorOpenResult> OpenAsync(
        string slug, string relativePath, int line, LineOrigin origin = LineOrigin.Anclada,
        int originalLine = 0, CancellationToken ct = default)
        => WithTimeout(() => Open(slug, relativePath, line, origin, originalLine), LaunchTimeout, ct);

    /// <summary>
    /// El tope de tiempo, aislado de todo lo que toca el sistema para poder probarlo: un arranque
    /// que no vuelve tiene que resolverse en <c>false</c>, no colgar a quien espera.
    /// </summary>
    internal static async Task<bool> WithTimeout(Func<bool> launch, TimeSpan timeout, CancellationToken ct = default)
    {
        Task<bool> running = Task.Run(launch, CancellationToken.None);
        Task finished = await Task.WhenAny(running, Task.Delay(timeout, ct)).ConfigureAwait(false);

        // El arranque que se pasó de tiempo sigue su curso en segundo plano —no hay forma de
        // abortar un Process.Start a medias—, pero la interfaz ya no lo espera.
        return await running.ConfigureAwait(false);
    }

    /// <inheritdoc cref="WithTimeout(Func{bool}, TimeSpan, CancellationToken)"/>
    internal static async Task<T> WithTimeout<T>(Func<T> launch, TimeSpan timeout, CancellationToken ct = default)
    {
        Task<T> running = Task.Run(launch, CancellationToken.None);
        Task finished = await Task.WhenAny(running, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return await running.ConfigureAwait(false);
    }

    /// <summary>
    /// Abre <paramref name="relativePath"/> del clon de <paramref name="slug"/>, en
    /// <paramref name="line"/>, con el editor configurado.
    /// </summary>
    public EditorOpenResult Open(
        string slug, string relativePath, int line,
        LineOrigin origin = LineOrigin.Anclada, int originalLine = 0)
    {
        string? clone = _machines.Load().ClonePathFor(slug);
        if (string.IsNullOrWhiteSpace(clone))
        {
            return EditorOpenResult.Failed(
                EditorRegistry.NameOf(_settings.Current.Editor),
                "no hay clon de esta aplicación en esta máquina");
        }

        string abs = Path.Combine(clone!, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return OpenPath(abs, line, origin, originalLine);
    }

    /// <summary>
    /// Abre una ruta absoluta. Lo usa «Probar» de Ajustes (§2), que abre un fichero del repositorio
    /// de la aplicación activa en una línea conocida — la única forma de saber que el editor
    /// funciona <b>antes</b> de necesitarlo.
    /// </summary>
    public EditorOpenResult OpenPath(
        string absolutePath, int line, LineOrigin origin = LineOrigin.Anclada, int originalLine = 0)
    {
        // El ajuste se lee AQUÍ, en cada pulsación: es lo que hace que «se aplica la próxima vez
        // que abras código» sea verdad y no una frase de la ayuda.
        string configured = _settings.Current.Editor;
        EditorDefinition? editor = EditorRegistry.Find(configured);
        if (editor is null)
        {
            return EditorOpenResult.Failed(
                EditorRegistry.NameOf(configured),
                $"«{configured}» no es ningún editor conocido; revisa el ajuste en Ajustes › Avanzado");
        }

        string? executable = editor.Kind == EditorKind.Program ? _detector.Locate(editor) : null;
        if (editor.Kind == EditorKind.Program && executable is null)
        {
            // Desinstalado, o nunca instalado. NO se cae a otro editor: se dice.
            return EditorOpenResult.Failed(
                editor.Name,
                $"{editor.Name} no está en esta máquina; revisa el editor en Ajustes › Avanzado");
        }

        EditorCommand command = EditorRegistry.Build(editor, executable, absolutePath, line);

        if (!command.Ok)
        {
            return EditorOpenResult.Failed(editor.Name, command.Error!);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = command.UseShell,
            });
        }
        catch (Exception ex)
        {
            return EditorOpenResult.Failed(editor.Name, ex.Message.Trim(), command.Display);
        }

        return new EditorOpenResult(
            Opened: true,
            editor.Name,
            command.Display,
            line,
            command.CarriesLine,
            command.NoLineReason,
            null,
            origin,
            originalLine);
    }

    /// <summary>
    /// La prueba del botón «Probar» de Ajustes (§2): abre un fichero <b>de verdad</b> en una línea
    /// conocida y cuenta qué comando se lanzó y si volvió.
    /// <para>
    /// Es la única forma de que el usuario sepa que su editor funciona <b>antes</b> de necesitarlo
    /// —y de que un comando personalizado mal escrito se vea al escribirlo y no tres días después,
    /// delante de un hallazgo.
    /// </para>
    /// </summary>
    /// <param name="slug">La aplicación activa. Sin ella se prueba con un fichero propio.</param>
    /// <param name="fallbackFile">El fichero al que recurrir cuando no hay clon a mano.</param>
    public Task<EditorOpenResult> TestAsync(string? slug, string fallbackFile, CancellationToken ct = default)
        => WithTimeout(() => Test(slug, fallbackFile), LaunchTimeout, ct);

    /// <inheritdoc cref="TestAsync"/>
    public EditorOpenResult Test(string? slug, string fallbackFile)
    {
        string? clone = slug is { Length: > 0 } ? _machines.Load().ClonePathFor(slug) : null;
        string? file = clone is { Length: > 0 } ? FirstReadableFile(clone!) : null;
        file ??= File.Exists(fallbackFile) ? fallbackFile : null;

        if (file is null)
        {
            return EditorOpenResult.Failed(
                EditorRegistry.NameOf(_settings.Current.Editor),
                "no hay ningún fichero con el que probar: abre una aplicación con su clon vinculado");
        }

        return OpenPath(file, TestLine(file));
    }

    /// <summary>
    /// Una línea que <b>existe</b> en ese fichero. Mandar a la 10 de un fichero de tres líneas
    /// haría fallar la prueba por culpa de la prueba.
    /// </summary>
    internal static int TestLine(string file)
    {
        try
        {
            return Math.Max(1, Math.Min(10, File.ReadLines(file).Count()));
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>El primer fichero de código del clon, saltándose lo que no es código del proyecto.</summary>
    private static string? FirstReadableFile(string clone)
    {
        string[] extensions = [".cs", ".ts", ".js", ".java", ".py", ".xaml", ".md"];
        try
        {
            return Directory.EnumerateFiles(clone, "*.*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <b>Lo que dice el toast</b> (§3), en una función pura: qué editor, qué línea, y de dónde
    /// salió esa línea. Se prueba sin abrir nada.
    /// </summary>
    public static string Toast(EditorOpenResult result)
    {
        if (!result.Opened)
        {
            return $"No se pudo abrir en {result.EditorName}: {result.Failure}.";
        }

        string where = result.LineDelivered
            ? $"línea {result.Line}"
            : $"sin ir a la línea {result.Line} ({result.NoLineReason})";

        string provenance = result.Origin switch
        {
            LineOrigin.Reanclada when result.OriginalLine > 0 && result.OriginalLine != result.Line
                => $" (antes {result.OriginalLine})",
            LineOrigin.SinAnclar => " (original; la unidad ha cambiado)",
            _ => string.Empty,
        };

        return $"Abierto en {result.EditorName} · {where}{provenance}";
    }
}
