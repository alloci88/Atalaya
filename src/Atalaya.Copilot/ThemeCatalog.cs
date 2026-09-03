using System.Text;
using Atalaya.Domain;

namespace Atalaya.Copilot;

/// <summary>
/// El catálogo de TEMÁTICAS de la casa (F17 §1): qué busca cada lupa y qué NO debe reportar.
/// <para>
/// Vive aquí, junto a <see cref="SeverityRubric"/>, y con el mismo régimen: un solo sitio,
/// versionado en git, que se CITA desde el prompt y no se copia. Es un catálogo cerrado —seis
/// entradas— porque cada temática es un encargo que el auditor tiene que poder cumplir sin
/// interpretar: una temática personalizada sería un prompt libre, y un prompt libre no es una
/// lupa, es otra auditoría general con otro nombre. Las personalizadas quedan en BACKLOG.
/// </para>
/// <para>
/// <b>La regla dura del enfoque.</b> Con una temática concreta, fuera de ella NO se reporta nada.
/// Ni «también he visto esto», ni las críticas de otra familia: un enfoque que además mira otras
/// cosas no es un enfoque, y para la mirada completa existe el ciclo General. La regla se le
/// escribe al auditor en el prompt y la aplicación la sostiene en la reconciliación: un veredicto
/// sobre un hallazgo de otra temática se rechaza con error tipado (F17 §3).
/// </para>
/// <para>
/// <b>Lo que la temática NO cambia.</b> Ni la rúbrica de severidad —un secreto en claro es crítico
/// en un ciclo de Seguridad exactamente igual que en uno General— ni la gobernanza: silencios,
/// patrones, directivas y la guarda de evidencia aplican por aplicación, sin mirar la lupa.
/// </para>
/// </summary>
public static class ThemeCatalog
{
    /// <summary>La temática recomendada, preseleccionada en cada diálogo: la mirada completa.</summary>
    public const AuditTheme Recommended = AuditTheme.General;

    /// <summary>Las seis, en el orden en que se ofrecen: la General primero, que es la de referencia.</summary>
    public static IReadOnlyList<AuditTheme> All { get; } = new[]
    {
        AuditTheme.General,
        AuditTheme.Seguridad,
        AuditTheme.Rendimiento,
        AuditTheme.Fiabilidad,
        AuditTheme.Concurrencia,
        AuditTheme.Mantenibilidad,
    };

    /// <summary>El nombre que se lee en la interfaz, los informes y el prompt.</summary>
    public static string Display(AuditTheme theme) => theme switch
    {
        AuditTheme.General => "General",
        AuditTheme.Seguridad => "Seguridad",
        AuditTheme.Rendimiento => "Rendimiento",
        AuditTheme.Fiabilidad => "Fiabilidad",
        AuditTheme.Concurrencia => "Concurrencia y asincronía",
        AuditTheme.Mantenibilidad => "Mantenibilidad",
        _ => theme.ToString(),
    };

    /// <summary>Una línea para el diálogo: qué significa elegirla.</summary>
    public static string Description(AuditTheme theme) => theme switch
    {
        AuditTheme.General =>
            "El criterio completo de siempre: todo el catálogo de hallazgos. Es el ciclo de referencia; "
            + "un ciclo temático no lo sustituye.",
        AuditTheme.Seguridad =>
            "Secretos y credenciales, validación de entradas, inyección, transporte inseguro, permisos "
            + "y criptografía casera. Nada más.",
        AuditTheme.Rendimiento =>
            "Algoritmia innecesariamente cara, asignaciones y colecciones ineficientes, E/S y llamadas "
            + "redundantes, recursos que no se reutilizan. Nada más.",
        AuditTheme.Fiabilidad =>
            "Nulos, índices, excepciones tragadas o sin manejar, casos límite, contratos incumplidos y "
            + "gestión de recursos (using/dispose). Nada más.",
        AuditTheme.Concurrencia =>
            "Carreras, bloqueos, .Result/.Wait, estado compartido mutable y deadlocks. Nada más.",
        AuditTheme.Mantenibilidad =>
            "Duplicación, nomenclatura, documentación que miente, complejidad y tamaño, código muerto. "
            + "Nada más.",
        _ => string.Empty,
    };

    /// <summary>
    /// Qué BUSCA la lupa, para el prompt del auditor. Cada punto es un tipo de defecto concreto,
    /// redactado como se redactó la rúbrica: con ejemplos que se reconocen en el código, no con
    /// categorías que haya que interpretar. General no tiene lista: su criterio es el brief entero.
    /// </summary>
    public static string Looks(AuditTheme theme) => theme switch
    {
        AuditTheme.Seguridad => """
            - Secretos y credenciales en el código o en la configuración versionada: contraseñas,
              tokens, claves de API, cadenas de conexión con usuario y clave, certificados privados.
            - Entradas sin validar o sin acotar antes de usarse: parámetros de red, ficheros, argumentos
              de línea de órdenes, cabeceras, deserialización de datos que vienen de fuera.
            - Inyección: SQL concatenado, órdenes de shell o de proceso construidas con texto ajeno,
              LDAP, XPath, rutas de fichero sin normalizar (path traversal), HTML sin escapar (XSS).
            - Transporte inseguro: HTTP donde tocaba HTTPS, validación de certificados desactivada,
              TLS antiguo forzado, cookies sin Secure/HttpOnly.
            - Permisos y autorización: comprobaciones que faltan o que se hacen solo en el cliente,
              recursos accesibles sin comprobar a quién pertenecen, privilegios más amplios de lo que
              hace falta.
            - Criptografía casera o rota: algoritmos propios, MD5/SHA-1 para contraseñas, claves fijas
              en el código, IV o sal reutilizados, aleatoriedad no criptográfica donde se necesita.
            """,
        AuditTheme.Rendimiento => """
            - Algoritmia innecesariamente cara: doble recorrido donde bastaba uno, búsqueda lineal
              repetida sobre la misma colección, O(n²) evitable con un diccionario o un conjunto,
              ordenaciones que no hacían falta.
            - Asignaciones y colecciones ineficientes: listas que se copian para nada, concatenación
              de cadenas en bucle, boxing en rutas calientes, LINQ diferido reevaluado varias veces,
              colecciones sin capacidad inicial cuando el tamaño se conoce.
            - E/S y llamadas redundantes: el mismo fichero leído varias veces, consultas dentro de un
              bucle (N+1), llamadas de red repetidas con el mismo argumento, serializaciones
              intermedias que no aportan.
            - Recursos que no se reutilizan: clientes HTTP, conexiones, expresiones regulares o
              buffers creados en cada llamada cuando podían compartirse; cachés que faltan donde el
              cálculo es caro y estable.
            """,
        AuditTheme.Fiabilidad => """
            - Nulos: desreferencias sin comprobar, valores opcionales tratados como obligatorios,
              resultados de búsqueda usados sin mirar si encontraron algo.
            - Índices y límites: off-by-one, acceso fuera de rango, bucles que asumen colecciones no
              vacías, particiones de cadenas que asumen un formato.
            - Excepciones tragadas o sin manejar: catch vacío, catch genérico que oculta la causa,
              errores convertidos en valores por defecto sin registrar, fallos parciales que dejan
              el estado a medias.
            - Casos límite: cero, negativo, colección vacía, cadena vacía, fecha en el cambio de año o
              de zona horaria, redondeos y precisión en cálculos.
            - Contratos incumplidos: el método no hace lo que promete su nombre o su documentación,
              precondiciones que no se comprueban, valores devueltos ambiguos.
            - Gestión de recursos: IDisposable sin using, ficheros o conexiones que no se cierran en
              la ruta de error, transacciones sin confirmar ni deshacer.
            """,
        AuditTheme.Concurrencia => """
            - Carreras: estado compartido leído y escrito desde varios hilos sin sincronizar,
              comprobar-y-actuar sin atomicidad, colecciones no seguras para hilos usadas desde
              varios.
            - Bloqueos sobre asincronía: .Result, .Wait(), .GetAwaiter().GetResult() en código que
              podía esperar con await; async void fuera de manejadores de eventos.
            - Estado compartido mutable: estáticos que se escriben, singletons con estado por
              petición, campos de instancia mutados desde tareas paralelas.
            - Deadlocks y bloqueos: locks anidados en orden distinto, esperas dentro de un lock,
              contexto de sincronización capturado donde no debía (ConfigureAwait), semáforos que
              no se liberan en la ruta de error.
            - Cancelación y tiempo de vida: tokens de cancelación ignorados, tareas lanzadas y
              olvidadas (fire-and-forget) cuyo fallo nadie observa, temporizadores sin desechar.
            """,
        AuditTheme.Mantenibilidad => """
            - Duplicación: el mismo bloque copiado con variaciones mínimas, lógica repetida que
              tendría que ser una sola función, constantes repetidas por valor.
            - Nomenclatura: nombres que mienten o no dicen nada (tmp, data2, DoStuff), abreviaturas
              opacas, nombres que contradicen el tipo o el contenido.
            - Documentación que miente: comentarios que describen lo que el código YA no hace,
              XML-doc con parámetros que no existen, TODO que ya se hizo.
            - Complejidad y tamaño: métodos de cientos de líneas, anidamientos profundos, booleanos
              de control que cambian el comportamiento de un método entero, clases que hacen de todo.
            - Código muerto: métodos, campos y ramas a los que nadie llega; parámetros que no se
              usan; código comentado que se dejó «por si acaso».
            """,
        _ => string.Empty,
    };

    /// <summary>
    /// Qué NO debe reportar la lupa: lo que un auditor con esta temática se encontrará y tendría
    /// que callarse. Se nombra lo concreto —lo que de verdad va a ver en el código— y no solo «el
    /// resto», porque un modelo al que solo se le dice «no mires otras cosas» acaba reportando
    /// justo la crítica de al lado con la excusa de que era importante.
    /// </summary>
    public static string Excludes(AuditTheme theme) => theme switch
    {
        AuditTheme.Seguridad => """
            NO reportes, aunque lo veas: defectos de rendimiento (recorridos dobles, asignaciones),
            nulos y excepciones sin manejar que no abran una brecha, carreras y bloqueos, duplicación,
            nombres, comentarios desfasados ni código muerto. Una credencial en claro sí es tuya;
            una desreferencia nula que solo tumba el proceso, no.
            """,
        AuditTheme.Rendimiento => """
            NO reportes, aunque lo veas: credenciales o secretos en el código, entradas sin validar,
            nulos, índices y excepciones sin manejar, carreras y bloqueos (salvo que el defecto sea
            un coste medible —un .Result que serializa lo que podía ir en paralelo sí es tuyo—),
            duplicación, nombres, documentación ni código muerto.
            """,
        AuditTheme.Fiabilidad => """
            NO reportes, aunque lo veas: secretos ni inyección, coste o eficiencia (un recorrido
            doble que da el resultado correcto no es tuyo), carreras y bloqueos entre hilos,
            duplicación, nombres, documentación desfasada ni código muerto.
            """,
        AuditTheme.Concurrencia => """
            NO reportes, aunque lo veas: secretos ni inyección, eficiencia que no tenga que ver con
            hilos ni con esperas, nulos e índices en código secuencial, excepciones tragadas que no
            oculten el fallo de una tarea, duplicación, nombres, documentación ni código muerto.
            """,
        AuditTheme.Mantenibilidad => """
            NO reportes, aunque lo veas: secretos ni inyección, coste o eficiencia, nulos, índices y
            excepciones sin manejar, carreras y bloqueos. Que un método sea difícil de leer sí es
            tuyo; que además tenga un fallo funcional no lo es —eso lo recoge un ciclo General o el
            de su familia—.
            """,
        _ => string.Empty,
    };
}

/// <summary>
/// El bloque de temática del prompt del auditor (F17 §1). Con General no escribe NADA: el prompt
/// de un ciclo General es, byte a byte, el mismo que antes de F17 — es lo que garantiza que el
/// ciclo General se comporte exactamente como hoy.
/// <para>
/// <b>Apretado en R1, como F24 apretó el suyo.</b> Este bloque viaja en el prefijo estable, o sea
/// en TODAS las llamadas de la sesión: pesaba 549 tokens bajo Seguridad y pesa 451. Lo que se
/// recortó son PALABRAS, no criterio — las dos listas del catálogo (D-824) y la regla dura del
/// enfoque (D-825) siguen enteras, y la de reconciliación se dice una vez aquí en lugar de dos,
/// porque el bloque de hallazgos de otras temáticas la repite justo donde está la lista.
/// </para>
/// </summary>
public static class ThemeSection
{
    /// <summary>La cabecera del bloque. Los tests la buscan para afirmar que la temática viajó.</summary>
    public const string Heading = "ENFOQUE DEL CICLO";

    public static string Render(AuditTheme theme)
    {
        if (theme == AuditTheme.General)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{Heading}: {ThemeCatalog.Display(theme).ToUpperInvariant()}.");
        sb.AppendLine("Ciclo TEMÁTICO. Fuera de esta familia NO SE REPORTA NADA —ni con submit_findings ni con");
        sb.AppendLine("add_locations, por grave que te parezca—: la mirada completa la hace el ciclo General. La");
        sb.AppendLine("rúbrica de severidad no cambia.");
        sb.AppendLine();
        sb.AppendLine("QUÉ BUSCAS:");
        sb.AppendLine(ThemeCatalog.Looks(theme).TrimEnd());
        sb.AppendLine();
        sb.AppendLine("QUÉ NO ES TUYO:");
        sb.AppendLine(ThemeCatalog.Excludes(theme).TrimEnd());
        sb.AppendLine();
        sb.AppendLine("RECONCILIACIÓN ACOTADA: en report_verdicts te pronuncias SOLO sobre los existentes de tu");
        sb.AppendLine("temática (la lista de abajo); un veredicto sobre uno de otra se rechaza.");
        return sb.ToString();
    }
}

/// <summary>Un color de temática con su paso para cada tema (claro / oscuro).</summary>
public sealed record ThemeColor(string Light, string Dark)
{
    public string For(bool dark) => dark ? Dark : Light;
}

/// <summary>
/// La paleta categórica de las TEMÁTICAS (F17 §6), fija y documentada junto al catálogo, que es
/// donde se decide qué significa cada una.
/// <para>
/// <b>Cómo se eligió, y por qué no «a ojo».</b> Seis valores distinguibles entre sí, que no
/// colisionen con los cuatro de severidad (<c>#D13A3A</c>, <c>#E07A2B</c>, <c>#D2B036</c>,
/// <c>#6C93C0</c> siguen reservados a chips y roscos: aquí no hay ni rojo, ni naranja, ni amarillo,
/// ni el azul-gris de Baja) y legibles en los dos temas. Cada uno tiene DOS pasos elegidos por
/// separado —el mismo criterio que <c>SeriesColor</c> (F5.9) y que la rampa del mapa (F10.1)—,
/// y cada paso se comprobó contra su superficie: contraste ≥ 3,0 (el mínimo de WCAG para objetos
/// gráficos) sobre <c>#F6F7FA</c> en claro y sobre <c>#12151D</c> en oscuro. Un test mide las tres
/// cosas —contraste, distancia entre temáticas y distancia a cada severidad— para que cambiar un
/// hex sin volver a medir salte en la build.
/// </para>
/// <para>
/// General va en un neutro sobrio: es «lo de siempre», y la cinta tiene que dejar que las lupas
/// específicas destaquen sobre él.
/// </para>
/// </summary>
public static class ThemePalette
{
    /// <summary>El fondo contra el que se midió cada paso. Los tests lo usan; nada más lo lee.</summary>
    public const string LightSurface = "#F6F7FA";

    /// <inheritdoc cref="LightSurface"/>
    public const string DarkSurface = "#12151D";

    public static ThemeColor Of(AuditTheme theme) => theme switch
    {
        AuditTheme.General => new("#6B7280", "#9CA3AF"),         // gris neutro
        AuditTheme.Seguridad => new("#7C3AED", "#B392F0"),       // violeta
        AuditTheme.Rendimiento => new("#0E7C86", "#45C4CF"),     // verde azulado
        AuditTheme.Fiabilidad => new("#25784A", "#6CCB93"),      // verde
        AuditTheme.Concurrencia => new("#B0227A", "#F07FBF"),    // fucsia
        AuditTheme.Mantenibilidad => new("#1E3A8A", "#7FB2FF"), // azul marino / azul claro
        _ => new("#6B7280", "#9CA3AF"),
    };

    /// <summary>El hex de una temática en un tema concreto.</summary>
    public static string Hex(AuditTheme theme, bool dark) => Of(theme).For(dark);
}
