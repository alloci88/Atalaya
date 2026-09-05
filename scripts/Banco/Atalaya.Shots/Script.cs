using System.IO;
using Atalaya.Agents;
using Atalaya.Copilot;

namespace Atalaya.Shots;

/// <summary>
/// El guion del agente falso. Los hallazgos son los de la sesión real que capturó el usuario, con
/// sus títulos: lo que se va a mirar es cómo se ven en pantalla, y un «lorem ipsum» no ocupa lo
/// que ocupa una frase de verdad.
/// <para>
/// El barrido devuelve algo en la primera pasada de cada unidad y nada en las siguientes, que es
/// lo que hace que la sesión converja en vez de dar vueltas.
/// </para>
/// </summary>
public static class Script
{
    private static readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lo olvida todo. El banco monta CUATRO sesiones (dos temas × dos tamaños) en el mismo
    /// proceso, y sin esto la segunda encontraría todas las unidades ya vistas y no reportaría
    /// nada: las capturas 2, 3 y 4 saldrían vacías.
    /// </summary>
    public static void Reset() => Seen.Clear();

    public static IEnumerable<SubmitFindingArgs> Audit(AuditUnitRequest request)
    {
        // Un pelín de espera EN CADA llamada —también en las pasadas secas—: sin ella la sesión
        // entera dura menos de lo que tarda la ventana en pintarse, y no hay forma de
        // fotografiarla en vivo, que es de lo que va.
        Thread.Sleep(800);

        // Una pasada productiva por unidad; las siguientes, secas.
        if (!Seen.Add(request.UnitPath))
        {
            return Array.Empty<SubmitFindingArgs>();
        }

        string file = request.UnitPath;
        Lines = request.UnitContent;

        return file switch
        {
            var f when f.EndsWith("ClienteRemoto.cs", StringComparison.OrdinalIgnoreCase) => new[]
            {
                F("errores.seguridad.secreto-en-codigo", "Errores", "Critica",
                  "Credenciales de servicio embebidas en el código",
                  "UsuarioServicio y ClaveServicio son constantes con usuario y contraseña reales del "
                  + "servicio corporativo, versionadas en el fuente. Cualquiera con acceso al repositorio "
                  + "tiene las credenciales.",
                  "Cualquiera con acceso de lectura al repositorio obtiene credenciales de producción. "
                  + "Rotarlas obliga a recompilar y desplegar.",
                  "Mover las credenciales a configuración segura (User Secrets, variables de entorno, "
                  + "Key Vault) e inyectarlas mediante IOptions/IConfiguration.",
                  f, 11, "ClienteRemoto"),
                F("errores.concurrencia.race", "Errores", "Alta",
                  "Mutación de DefaultRequestHeaders en HttpClient estático compartido",
                  "DescargarParte añade la cabecera Authorization a un HttpClient estático en cada "
                  + "llamada. Las cabeceras por defecto no son seguras entre hilos y se acumulan.",
                  "Cabeceras duplicadas, credenciales de una petición viajando en otra, y excepciones "
                  + "intermitentes bajo concurrencia.",
                  "Usar HttpRequestMessage por petición y no tocar DefaultRequestHeaders de un cliente "
                  + "compartido.",
                  f, 22, "ClienteRemoto.DescargarParte"),
                F("errores.async.mal-usado", "Errores", "Alta",
                  "Uso de .Result bloqueante sobre operación async",
                  "GetAsync().Result y ReadAsStringAsync().Result bloquean el hilo llamante.",
                  "Deadlock en contextos con sincronización y agotamiento del pool de hilos bajo carga.",
                  "Hacer el método async y usar await en las dos llamadas.",
                  f, 33, "ClienteRemoto.DescargarParte"),
                F("criterio.observabilidad", "Mejoras", "Media",
                  "No se comprueba el código de estado HTTP",
                  "La respuesta se lee sin mirar IsSuccessStatusCode: un 500 se procesa como si fuera "
                  + "un parte válido.",
                  "Errores del servicio remoto entran silenciosamente como datos.",
                  "Comprobar el estado y devolver un resultado explícito ante fallo.",
                  f, 27, "ClienteRemoto.DescargarParte"),
                F("criterio.testabilidad", "Mejoras", "Baja",
                  "URL base y HttpClient no inyectables dificultan pruebas",
                  "El HttpClient es un campo estático y la URL llega por constructor pero no hay costura "
                  + "para sustituir el transporte.",
                  "No se puede probar el cliente sin red.",
                  "Inyectar IHttpClientFactory y una abstracción del transporte.",
                  f, 14, "ClienteRemoto"),
            },
            var f when f.EndsWith("MotorCalculoLegacy.cs", StringComparison.OrdinalIgnoreCase) => new[]
            {
                F("errores.calculo.negocio", "Errores", "Alta",
                  "Off-by-one en CargaTotalKg provoca IndexOutOfRangeException",
                  "El bucle recorre con `i <= barrenos.Length`, un índice más allá del último elemento.",
                  "Excepción no controlada en cada cálculo de carga total.",
                  "Recorrer con `i < barrenos.Length`, o usar barrenos.Sum().",
                  f, 11, "MotorCalculoLegacy.CargaTotalKg"),
                F("errores.calculo.negocio", "Errores", "Critica",
                  "El total histórico aplica un factor de roca fijo por barreno y después vuelve a "
                  + "multiplicar por el tipo solicitado",
                  "FactorRoca se aplica dos veces sobre la misma magnitud.",
                  "Los totales históricos salen inflados y no cuadran con los partes en papel.",
                  "Aplicar el factor una sola vez, en el punto de agregación.",
                  f, 16, "MotorCalculoLegacy.FactorRoca"),
                F("criterio.mantenibilidad", "Mejoras", "Media",
                  "El tipo de roca se consulta sin validación de dominio ni error controlado",
                  "Un tipo desconocido devuelve 1.0 en silencio en vez de fallar.",
                  "Un dato mal escrito produce un cálculo válido en apariencia.",
                  "Validar contra el catálogo de tipos y fallar explícitamente.",
                  f, 16, "MotorCalculoLegacy.FactorRoca"),
            },
            var f when f.EndsWith("ConversorHex.cs", StringComparison.OrdinalIgnoreCase) => new[]
            {
                F("errores.entrada.no-validada", "Errores", "Alta",
                  "ADecimal no valida la entrada y revienta con cualquier texto no hexadecimal",
                  "Convert.ToInt64(hex, 16) lanza FormatException con entradas del usuario.",
                  "Excepción no controlada al importar un fichero con un campo mal formado.",
                  "Usar long.TryParse con NumberStyles.HexNumber y devolver un resultado explícito.",
                  f, 7, "ConversorHex.ADecimal"),
                F("criterio.contrato", "Mejoras", "Media",
                  "Las variantes aceptan medidas físicas no positivas y devuelven cálculos válidos "
                  + "aparentes",
                  "No hay precondición sobre el rango de entrada.",
                  "Cálculos silenciosamente incorrectos aguas abajo.",
                  "Declarar y comprobar las precondiciones.",
                  f, 12, "ConversorHex"),
            },
            var f when f.EndsWith("FormateadorInforme.cs", StringComparison.OrdinalIgnoreCase) => new[]
            {
                F("errores.cultura", "Errores", "Media",
                  "Formato de fecha y concatenación dependientes de la cultura del hilo",
                  "ToString(\"dd/MM/yyyy\") sin IFormatProvider usa la cultura actual.",
                  "El mismo informe sale distinto según la máquina que lo genera.",
                  "Pasar CultureInfo explícita.",
                  f, 6, "FormateadorInforme.Encabezado"),
                F("criterio.legibilidad", "Mejoras", "Baja",
                  "Concatenación con + en vez de interpolación dificulta leer el formato",
                  "Las plantillas quedan repartidas entre operadores.",
                  "Cambiar el formato obliga a releer la expresión entera.",
                  "Usar interpolación de cadenas.",
                  f, 9, "FormateadorInforme.Linea"),
            },
            var f when f.EndsWith("RepositorioVoladuras.cs", StringComparison.OrdinalIgnoreCase) => new[]
            {
                F("errores.entrada.no-validada", "Errores", "Media",
                  "Buscar indexa la lista sin comprobar el rango",
                  "_cache[indice] lanza ArgumentOutOfRangeException con cualquier índice fuera.",
                  "Excepción no controlada desde la interfaz.",
                  "Comprobar el rango y devolver un resultado explícito.",
                  f, 9, "RepositorioVoladuras.Buscar"),
                F("criterio.concurrencia", "Mejoras", "Baja",
                  "La caché es una List<T> sin sincronización",
                  "Guardar y Buscar pueden correr en hilos distintos.",
                  "Corrupción de la lista bajo concurrencia.",
                  "Usar una colección concurrente o sincronizar el acceso.",
                  f, 5, "RepositorioVoladuras"),
            },
            _ => new[]
            {
                F("criterio.duplicacion", "Mejoras", "Media",
                  "La unidad concentra conversiones casi idénticas en lugar de una abstracción común",
                  "Tres métodos con la misma forma y distinta constante.",
                  "Añadir una conversión nueva obliga a copiar la anterior.",
                  "Extraer una tabla de factores.",
                  file, 5, "Conversiones"),
                F("criterio.contrato", "Mejoras", "Baja",
                  "Las conversiones no declaran el rango válido de entrada",
                  "Aceptan negativos sin decir nada.",
                  "Magnitudes físicas imposibles pasan sin aviso.",
                  "Declarar precondiciones.",
                  file, 7, "Conversiones"),
            },
        };
    }

    /// <summary>El contenido de la unidad que se está auditando, para poder anclar de verdad.</summary>
    private static string Lines = string.Empty;

    private static SubmitFindingArgs F(
        string rule, string pillar, string severity, string title, string description,
        string impact, string recommendation, string path, int line, string symbol)
        => new(rule, pillar, severity, title, description, impact, recommendation,
               new[] { new SubmitLocation(path, Clamp(line), Snippet(line)) }, symbol);

    /// <summary>
    /// La línea de verdad del fichero. Un auditor real manda el fragmento que vio, y el anclaje lo
    /// usa: sin él las ubicaciones se rechazan y la unidad sale «incompleta».
    /// </summary>
    private static string? Snippet(int line)
    {
        string[] all = Split();
        int i = Math.Clamp(line, 1, all.Length) - 1;
        return all[i].Trim() is { Length: > 0 } text ? text : null;
    }

    private static int Clamp(int line) => Math.Clamp(line, 1, Math.Max(1, Split().Length));

    private static string[] Split() => Lines.Split('\n');

    /// <summary>
    /// El guion del ARREGLO: narra, lee, edita y cierra con su sugerencia de commit. Es la forma
    /// que tiene una sesión real; lo que cambia es quién decide.
    /// </summary>
    /// <summary>
    /// El guion del arreglo, servido PASO A PASO y con pausa entre pasos. Devolverlo como array
    /// entero lo hace instantaneo, y una vez autorizado el fichero el arreglo pasaba de la tarjeta
    /// de permiso a la pantalla de cierre sin que hubiera un solo fotograma con la conversacion
    /// viva y el diff ya lleno, que es justo el minuto que hay que fotografiar.
    /// </summary>
    /// <summary>
    /// El guion del arreglo.
    /// <para>
    /// <b>El ritmo no se puede poner desde aqui.</b> El agente falso hace <c>ToArray()</c> del
    /// guion entero antes de emitir un solo paso, asi que una pausa entre <c>yield</c> se gasta
    /// toda de golpe al principio y el arreglo sigue pasando de la tarjeta de permiso a la
    /// pantalla de cierre sin un fotograma intermedio. Tampoco vale pausar: la pausa solo frena
    /// <c>apply_edit</c> y compilar, no el cierre.
    /// </para>
    /// <para>
    /// Lo que si para el arreglo en el sitio exacto es una <b>pregunta</b>: despues de tocar el
    /// fichero el agente plantea una decision de diseno y espera. El banco contesta a los permisos
    /// pero no a las decisiones, asi que la vista se queda donde hay que fotografiarla --el
    /// permiso ya concedido, el fichero tocado y el diff lleno-- y ademas es un estado REAL de la
    /// pantalla, no un truco de captura: un agente que pregunta a mitad de arreglo es justo lo que
    /// esta vista existe para enseñar.
    /// </para>
    /// </summary>
    public static IEnumerable<FixStep> Fix(FixRequest request) => new[]
    {
        new FixStep(Narration:
            "Voy a mover las credenciales fuera del código. El plan: quitar las dos constantes, "
            + "recibir la credencial por constructor, y dejar de mutar las cabeceras del HttpClient "
            + "compartido. Empiezo leyendo el fichero para ver todos sus usos."),
        new FixStep(Read: "src/AtalayaBanco.Core/Servicios/ClienteRemoto.cs"),
        new FixStep(Narration:
            "Confirmado: las constantes solo se usan en Credencial(). Sustituyo las dos por un campo "
            + "de solo lectura que llega por constructor."),
        new FixStep(
            Edit: new FixStepEdit(
                "src/AtalayaBanco.Core/Servicios/ClienteRemoto.cs",
                "Las credenciales dejan de estar en el fuente y entran por constructor.",
                new[]
                {
                    new FixEdit(
                        "    private const string UsuarioServicio = \"svc_partes\";\r\n"
                        + "    private const string ClaveServicio = \"P4rt3s!2024\";",
                        "    private readonly string _usuario;\r\n"
                        + "    private readonly string _clave;"),
                })),
        new FixStep(Narration:
            "Hecho. El constructor ahora pide usuario y clave, así que quien construya el cliente "
            + "tiene que sacarlos de configuración. He dejado el resto del método igual a propósito: "
            + "el bloqueo con .Result y la mutación de cabeceras son otros dos hallazgos, y mezclarlos "
            + "aquí haría el diff imposible de revisar."),
        new FixStep(
            Question:
                "Los otros dos hallazgos del fichero —el bloqueo con .Result y la mutación de "
                + "DefaultRequestHeaders— se arreglan en el mismo sitio. ¿Los meto en este cambio "
                + "o los dejo para su propio arreglo?",
            Choices: new[] { "Déjalos para su arreglo", "Métel los aquí" },
            AllowFreeform: true),
        new FixStep(Done: new FixDoneArgs(
            Summary:
                "Las credenciales salen del código fuente. El cliente las recibe por constructor y ya "
                + "no hay secretos versionados en este fichero.",
            CommitTitle: "Sacar las credenciales del servicio de partes del código fuente",
            CommitDescription:
                "UsuarioServicio y ClaveServicio eran constantes con credenciales reales, versionadas.\n"
                + "Ahora llegan por constructor para que salgan de configuración segura.\n\n"
                + "No se tocan el uso de .Result ni la mutación de DefaultRequestHeaders: son hallazgos\n"
                + "aparte y van en su propio cambio.",
            Risks:
                "Quien construya ClienteRemoto tiene que pasar las credenciales. Hay que revisar los "
                + "puntos de composición antes de desplegar.")),
    };
}
