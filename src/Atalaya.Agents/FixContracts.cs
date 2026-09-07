// Los contratos del ARREGLO ASISTIDO. Vivían en Atalaya.Copilot mientras arreglar era verdad de
// una sola casa (F14, D-775); desde F16 hay dos motores detrás del mismo contrato, así que bajan
// al vocabulario común. Lo que se movió fue el ensamblado, no una sola línea de comportamiento.

namespace Atalaya.Agents;

/// <summary>
/// Una edición puntual dentro de un fichero: sustituir <paramref name="OldText"/> por
/// <paramref name="NewText"/> (F6.9).
/// <para>
/// <b>Por qué buscar-y-sustituir y no líneas.</b> Un rango de líneas obliga al agente a acertar
/// números que ya no significan nada en cuanto aplica la primera edición del mismo fichero —y a
/// re-leerlo entre edición y edición—, que es exactamente el bucle de lecturas que el presupuesto
/// de <c>read_file</c> quiere evitar. Un fragmento literal se valida solo: si no aparece, o si
/// aparece más de una vez sin <paramref name="ReplaceAll"/>, la edición se rechaza con un error
/// que el agente puede corregir en vez de dejar el fichero a medias.
/// </para>
/// </summary>
/// <param name="OldText">El texto EXACTO que hay que encontrar. Vacío = crear el fichero.</param>
/// <param name="NewText">Lo que lo sustituye. Vacío = borrar ese fragmento.</param>
/// <param name="ReplaceAll">
/// Permite que el fragmento aparezca varias veces. Sin esto, la ambigüedad es un error: sustituir
/// «la primera aparición» de algo que sale tres veces es la forma más barata de romper un fichero.
/// </param>
public sealed record FixEdit(string OldText, string NewText, bool ReplaceAll = false);

/// <summary>Lo que devuelve <c>read_file</c>. Un fallo es un resultado, nunca una excepción.</summary>
/// <param name="Remaining">Lecturas que le quedan al agente. Va en la respuesta para que pueda
/// administrarse solo en vez de descubrir el tope cuando ya se lo ha gastado.</param>
/// <param name="TotalLines">Cuántas líneas tiene el fichero ENTERO.</param>
/// <param name="FirstLine">Primera línea de <paramref name="Content"/>, base 1.</param>
/// <param name="LastLine">Última línea de <paramref name="Content"/>, base 1.</param>
/// <param name="Notice">
/// Lo que falta por leer, dicho con letras (BUGFIX-LECTURA). Va aparte de
/// <paramref name="Content"/> —que es el fichero y nada más, para que los trozos concatenen byte a
/// byte— y solo aparece cuando queda fichero por detrás: un agente que ve un texto acabado a mitad
/// de línea y NO lee cuánto le falta, se lo inventa, y lo que se inventó una vez fue pedirle al
/// usuario que le pegara el resto.
/// </param>
public sealed record ReadFileResult(
    bool Ok,
    string? Content = null,
    string? Error = null,
    int Remaining = 0,
    int TotalLines = 0,
    int FirstLine = 0,
    int LastLine = 0,
    string? Notice = null);

/// <summary>Lo que devuelve <c>apply_edit</c>.</summary>
/// <param name="Denied">
/// El usuario dijo que NO. Se distingue de un error porque no es un fallo que reintentar: es una
/// decisión, y el agente tiene que replantear el arreglo, no volver a pedir lo mismo.
/// </param>
public sealed record ApplyEditResult(
    bool Applied, string? Error = null, int Changed = 0, bool Denied = false);

/// <summary>El resultado de compilar y pasar los tests, ya resumido por la aplicación.</summary>
public sealed record BuildAndTestResult(bool Ok, string Summary, bool TimedOut = false);

/// <summary>
/// El cierre del arreglo (<c>fix_done</c>): qué cambió, por qué, y la sugerencia de commit.
/// <para>
/// La app NO commitea (anti-objetivo declarado). Lo único que hace con esto es ahorrarle al humano
/// redactar el mensaje: título y descripción salen editables en la pantalla de cierre con un botón
/// de copiar.
/// </para>
/// </summary>
public sealed record FixDoneArgs(
    string Summary, string CommitTitle, string CommitDescription, string? Risks = null);

/// <summary>
/// Las CUATRO cosas que el agente puede hacer en una sesión de arreglo (F6.9 §3). No hay una
/// quinta: el <c>OnPermissionRequest</c> de la sesión rechaza todo lo demás —shell, git, red,
/// escritura directa— igual que en una auditoría.
/// </summary>
public interface IFixToolbox
{
    /// <summary>
    /// Lee un fichero del clon. Con presupuesto: explorar no es arreglar.
    /// <para>
    /// <b>Y con rango, para que ningún fichero quede fuera de alcance</b> (BUGFIX-LECTURA). Sin
    /// rango se devuelve el fichero entero si cabe, y si no, el primer trozo <b>diciendo cuántas
    /// líneas tiene y cuáles van</b>. El rango es solo para VER: se sigue editando por fragmento
    /// literal (D-545), porque un número de línea deja de significar nada en cuanto se aplica la
    /// primera edición.
    /// </para>
    /// </summary>
    /// <param name="startLine">Primera línea a devolver, base 1. Null = desde el principio.</param>
    /// <param name="endLine">Última línea a devolver, base 1. Null = hasta donde quepa.</param>
    ReadFileResult ReadFile(string path, int? startLine = null, int? endLine = null);

    /// <summary>
    /// La ÚNICA vía de modificación. Sobre los ficheros del hallazgo (y sus tests) se aplica
    /// directamente; sobre cualquier otro, la app le pregunta antes al usuario.
    /// </summary>
    ApplyEditResult ApplyEdit(string path, string reason, FixEdit[] edits);

    /// <summary>
    /// El agente lo SOLICITA; la app lo ejecuta. El agente jamás obtiene una shell.
    /// </summary>
    BuildAndTestResult RunBuildAndTests();

    /// <summary>Cierra la sesión. Terminal: después de esto el turno acaba.</summary>
    void FixDone(FixDoneArgs done);
}

/// <summary>Lo que se le encarga al agente en una sesión de arreglo.</summary>
/// <param name="CloneRoot">
/// La raíz del clon local. Es el <c>WorkingDirectory</c> de la sesión y el límite de todo lo que
/// el agente puede leer o tocar.
/// </param>
public sealed record FixRequest(string Prompt, string CloneRoot);

/// <summary>
/// El canal por el que la aplicación dirige una sesión de arreglo YA viva (F6.9 §4): el campo de
/// entrada del usuario y el botón «Detener».
/// </summary>
public interface IFixSteering
{
    /// <summary>
    /// Inyecta un mensaje del usuario en la sesión en curso. Devuelve <c>false</c> si el runtime
    /// no lo aceptó — entonces la app lo encola para el turno siguiente y lo DICE en la interfaz,
    /// que es lo honrado cuando no se puede prometer inmediatez.
    /// </summary>
    Task<bool> SendAsync(string message, CancellationToken ct);

    /// <summary>Aborta el turno en curso. Lo aplicado se queda; la sesión se cierra ordenadamente.</summary>
    Task AbortAsync(CancellationToken ct);
}

/// <summary>
/// Quién contesta a las preguntas del agente (la tool <c>ask_user</c> del runtime, que el SDK
/// entrega por <c>SessionConfig.OnUserInputRequest</c>).
/// <para>
/// <b>Verificado contra el SDK 1.0.11</b> (F6.9 §0). La documentación del paquete menciona además
/// <c>session.Ui.ConfirmAsync/SelectAsync/InputAsync</c>: existen, pero van en la dirección
/// CONTRARIA —es el SDK quien le pide algo al host— y lanzan si el host no declara la capacidad.
/// El camino agente → aplicación es este.
/// </para>
/// </summary>
public interface IUserQuestions
{
    /// <param name="choices">Opciones cerradas; vacío significa respuesta libre.</param>
    /// <param name="allowFreeform">El usuario puede escribir algo que no está en la lista.</param>
    /// <returns>La respuesta, o <c>null</c> si no se pudo obtener (sesión cancelada).</returns>
    Task<string?> AskAsync(
        string question, IReadOnlyList<string> choices, bool allowFreeform, CancellationToken ct);
}

/// <summary>
/// Todo lo que la aplicación pone del lado de una conversación de arreglo. Va en un solo objeto
/// porque son piezas de UNA cosa —la conversación— y una firma con seis parámetros sueltos se
/// equivoca de orden a la primera.
/// </summary>
/// <param name="NextTurn">
/// Se llama cuando el turno termina SIN que el agente haya cerrado con <c>fix_done</c>. Devolver
/// texto envía otro turno (es la vía de las órdenes del usuario que el runtime no aceptó a mitad);
/// devolver <c>null</c> cierra la sesión.
/// </param>
/// <param name="Ready">
/// Se invoca en cuanto la sesión existe, con el mando a distancia. Es lo que permite que la vista
/// tenga «Detener» y campo de entrada desde el primer segundo y no solo cuando el agente hable.
/// </param>
public sealed record FixConversation(
    IFixToolbox Toolbox,
    IUserQuestions Questions,
    Func<CancellationToken, Task<string?>>? NextTurn = null,
    Action<IFixSteering>? Ready = null);

/// <summary>
/// Un proveedor que además sabe ARREGLAR (H9, F6.9; segunda casa en F16).
/// <para>
/// <b>Sigue separado de <see cref="IAuditorProvider"/>, y ahora por un motivo mejor.</b> En F14
/// vivía en <c>Atalaya.Copilot</c> porque arreglar era verdad de una sola casa. Ya no lo es, así
/// que baja al vocabulario común — pero NO se funde con la interfaz del auditor: auditar y
/// arreglar son capacidades distintas, y un tercer proveedor futuro puede saber una y no la otra.
/// Que el compilador exija este tipo donde se arregla es lo que impide que elegir un auditor
/// desvíe por accidente un arreglo hacia quien no sabe hacerlo.
/// </para>
/// <para>
/// <b>Tiene implementación por defecto, y lanza.</b> Los proveedores de verdad la implementan; lo
/// repartido por los tests son dobles minúsculos que existen para ejercitar UN camino de
/// auditoría. Obligarlos a llevar un <c>FixAsync</c> vacío no probaría nada y serían copias
/// esperando a quedarse desfasadas. Lanzar dice la verdad: ese agente no sabe arreglar.
/// </para>
/// </summary>
public interface IAssistedFixProvider : IAuditorProvider
{
    /// <summary>
    /// Arregla UN hallazgo sobre el clon local, de forma interactiva (F6.9). A diferencia de
    /// auditar y verificar, aquí la sesión es una CONVERSACIÓN: dura varios turnos, el agente
    /// pregunta y el usuario puede dirigirla mientras corre.
    /// <para>
    /// La sesión se cierra cuando el agente llama a <c>fix_done</c> (tool terminal) o cuando
    /// <see cref="FixConversation.NextTurn"/> devuelve <c>null</c>. El agente edita SOLO por
    /// <c>apply_edit</c>: no tiene shell, ni git, ni red.
    /// </para>
    /// </summary>
    Task FixAsync(FixRequest request, FixConversation conversation, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} no implementa el arreglo asistido.");
}
