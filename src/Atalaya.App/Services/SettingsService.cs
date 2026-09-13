using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Los mínimos de los ajustes numéricos, escritos UNA vez (BUGFIX-AJUSTES).
/// <para>
/// Estaban repartidos como <c>Math.Max(15, …)</c> y <c>Math.Max(1, …)</c> por el view-model, la
/// carcasa y el arranque: tres sitios donde recordar el mismo número, y ninguno donde leerlo. Aquí
/// están los tres, y quien los aplica <b>avisa</b> — un valor corregido en silencio es
/// indistinguible de un ajuste que no ajusta, que es de lo que venía este parte.
/// </para>
/// </summary>
public static class SettingsLimits
{
    /// <summary>
    /// Un umbral de 0 líneas marcaría «grande» hasta un fichero vacío. Lo aplica la política de la
    /// aplicación (<c>ThresholdPolicyService</c>): desde F13 el umbral no se edita aquí, pero el
    /// mínimo se sigue escribiendo en un solo sitio.
    /// </summary>
    public const int MinLargeUnitLoc = 1;

    /// <summary>Y el mismo suelo por peso: un umbral por debajo de un carácter no dice nada.</summary>
    public const int MinLargeUnitChars = 1;

    /// <summary>Con 0 días todo hallazgo nacería viejo.</summary>
    public const int MinFreshnessDays = 1;

    /// <summary>Tope 1 = pasada única (D-097); 0 dejaría la auditoría sin hacer nada.</summary>
    public const int MinMaxPassesPerUnit = 1;

    /// <summary>
    /// El tope de fábrica: 4 pasadas que pueden aportar + las 2 secas que cierran el barrido
    /// (F16 §D). Ver <c>AppSettings.MaxPassesPerUnit</c> para la aritmética.
    /// </summary>
    public const int DefaultMaxPassesPerUnit = 6;

    /// <summary>
    /// El tope que traían las máquinas antes de F16. Se nombra para poder reconocerlo en la
    /// promoción única y NO tocar a quien eligió su propio número.
    /// </summary>
    public const int LegacyMaxPassesPerUnit = 5;

    /// <summary>Por debajo de 15 s el sondeo del hub se pisa a sí mismo.</summary>
    public const int MinPollingSeconds = 15;

    /// <summary>Menos de un minuto no le da al modelo tiempo ni a contestar.</summary>
    public const int MinCopilotTimeoutMinutes = 1;

    /// <summary>
    /// Aplica el mínimo y dice si hubo que aplicarlo. Devolver las dos cosas juntas es lo que
    /// permite que el que guarda pueda contarlo sin volver a comparar nada.
    /// </summary>
    public static int Clamp(int value, int minimum, out bool corrected)
    {
        corrected = value < minimum;
        return corrected ? minimum : value;
    }
}

/// <summary>
/// Los umbrales de ESTA máquina (§8 Ajustes). Nunca viajan al hub.
/// </summary>
public sealed class LocalThresholds
{
    /// <summary>
    /// A partir de cuántos días sin reconfirmarse un hallazgo se enseña «rancio» (§8, V3).
    /// <para>
    /// Personal, y con evidencia (F13 §2): lo único que hace es rellenar <c>FindingRow.IsStale</c>
    /// al reconstruir la lista. No se escribe en el hallazgo, no se publica y no se confunde con
    /// <c>NeedsReview</c> —que sí es del hub y lo escriben la reconciliación y la verificación—.
    /// Es una lente de lectura: dos compañeros con frescuras distintas ven el mismo hallazgo con
    /// distinto color y ninguno le cambia el estado al otro.
    /// </para>
    /// </summary>
    public int FreshnessDays { get; set; } = 60;

}

/// <summary>Machine-local application settings (§8 Ajustes). Never stored in the hub.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Legacy explicit git identity. Since F2 the identity is derived from the connected GitHub
    /// profile (D2.2) and this is only a fallback for users configured before F2.
    /// </summary>
    public string? GitUserName { get; set; }

    /// <summary>Legacy explicit git identity email. See <see cref="GitUserName"/>.</summary>
    public string? GitUserEmail { get; set; }

    /// <summary>
    /// Legacy per-user hub URL. Since F2 the hub comes from <c>appsettings.deploy.json</c> (D1);
    /// this is migrated once into <see cref="HubUrlOverride"/> when it differs, then cleared.
    /// </summary>
    public string? HubRepoUrl { get; set; }

    /// <summary>
    /// Advanced / development only: overrides the deployment hub URL. Shown collapsed in Ajustes.
    /// </summary>
    public string? HubUrlOverride { get; set; }

    /// <summary>
    /// DPAPI-protected Personal Access Token (base64). Since F2 this is the HIDDEN FALLBACK for
    /// teams whose organization blocks OAuth Apps; the account token always wins when present.
    /// </summary>
    public string? ProtectedPat { get; set; }

    /// <summary>True once the pre-F2 → F2 settings migration has run (D4).</summary>
    public bool ConnectionMigrated { get; set; }

    /// <summary>
    /// Restore libgit2's hard failure when a certificate's revocation status cannot be checked.
    /// Default false: Atalaya soft-fails that single condition (as browsers do) while still
    /// requiring a trusted, in-date certificate that matches the host, because corporate networks
    /// routinely block the CRL/OCSP responders. Advanced option.
    /// </summary>
    public bool RequireTlsRevocationCheck { get; set; }

    /// <summary>
    /// El editor de «Abrir en el editor» (§8), por su id del <c>EditorRegistry</c> (R13).
    /// <para>
    /// Hasta R13 solo valían <c>"vs"</c> y <c>"vscode"</c> porque eran los dos casos de un
    /// <c>if</c>; ahora es el id de una ficha del registro, y los ids viejos siguen valiendo
    /// —son los mismos dos— para no cambiarle el editor a nadie al actualizar.
    /// </para>
    /// </summary>
    public string Editor { get; set; } = EditorRegistry.VisualStudioId;

    /// <summary>"dark" or "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>
    /// <b>En qué divisa se enseña el coste</b> (F29 §2): <c>"credits"</c> o <c>"usd"</c>.
    /// <para>
    /// Es de ESTA máquina y no del hub, por la regla de F13: no gobierna nada compartido —el hub
    /// sigue guardando tokens y credits exactamente igual—, solo cómo lee las cifras quien está
    /// delante. Dos personas del mismo equipo pueden mirar el mismo panel en unidades distintas sin
    /// cambiarle el número a nadie.
    /// </para>
    /// <para>
    /// De fábrica, credits: es la unidad en la que factura GitHub y en la que grafica su panel, así
    /// que es la que se puede cuadrar contra la factura. Los dólares son para quien decide con un
    /// presupuesto, y eso se elige.
    /// </para>
    /// </summary>
    public string CostCurrency { get; set; } = "credits";

    /// <summary>
    /// Dónde y cómo estaba la ventana la última vez (F26 Parte A, D-956). Nunca viaja al hub: el
    /// tamaño de una ventana es de ESTA máquina y de este monitor.
    /// </summary>
    public WindowPlacement Window { get; set; } = new();

    public int PollingSeconds { get; set; } = 60;

    /// <summary>
    /// Lo que esta máquina mide para SÍ MISMA. Hoy solo la frescura (§8, V3), que es una lente de
    /// lectura: colorea la lista de hallazgos de quien mira y no escribe nada en el hub. Por la
    /// regla de F13 —lo que escribe estado compartido se gobierna con ajuste compartido— eso puede
    /// seguir siendo personal, y el umbral de tamaño no: aquel se mudó a
    /// <see cref="Thresholds"/> del <c>app.json</c>.
    /// <para>
    /// La CLAVE del fichero sigue siendo <c>defaultThresholds</c>: renombrarla habría tirado la
    /// frescura que cada máquina ya tiene puesta, y con ella el umbral heredado que la migración
    /// tiene que poder ofrecer.
    /// </para>
    /// </summary>
    /// <para>
    /// <b>Un valor viejo se ignora y desaparece, sin preguntar</b> (F26 §C, revisión). Entre
    /// <c>d859d16</c> y F13 el umbral de unidad grande fue un ajuste de esta máquina, y F13 dejó una
    /// oferta para llevarlo a la política de cada aplicación. Esa oferta se retira: la transición
    /// terminó hace tiempo y lo único que quedaba era un aviso que preguntaba por un número que ya
    /// no gobierna nada. No hace falta código de migración —<c>largeUnitLoc</c> ya no existe como
    /// propiedad, así que el deserializador lo ignora y el primer guardado lo borra del fichero—:
    /// borrarlo a mano habría sido escribir un migrador para no migrar nada.
    /// </para>
    /// </summary>
    [JsonPropertyName("defaultThresholds")]
    public LocalThresholds Thresholds { get; set; } = new();

    /// <summary>
    /// Interruptor del arreglo asistido (§5.7, H9 — entregado en F6.9).
    /// <para>
    /// <b>Encendido por defecto.</b> El flujo es supervisado por construcción: el agente narra lo
    /// que va a hacer antes de hacerlo, pide permiso fichero a fichero fuera del hallazgo, no
    /// tiene shell ni git ni red, y exige un árbol de trabajo limpio para que descartar devuelva
    /// el clon byte a byte. Nacer apagado escondería una capacidad segura detrás de un ajuste que
    /// nadie iba a encontrar. Y como el valor por defecto es <c>true</c>, las máquinas con un
    /// <c>settings.json</c> anterior a F6.9 —que no traen la clave— lo estrenan encendido.
    /// </para>
    /// </summary>
    public bool EnableAssistedFix { get; set; } = true;

    /// <summary>
    /// True una vez que la promoción única de <see cref="EnableAssistedFix"/> ya se ha aplicado
    /// en esta máquina (F6.9 §7). Misma forma que <see cref="ConnectionMigrated"/> y por la misma
    /// razón: una migración de ajustes tiene que dejar constancia de que corrió, o corre siempre.
    /// <para>
    /// Sin esta marca, apagar el interruptor a mano no duraría un reinicio: la promoción volvería
    /// a encenderlo. Con ella, la promoción ocurre exactamente una vez y a partir de ahí manda lo
    /// que diga el usuario.
    /// </para>
    /// </summary>
    public bool AssistedFixDefaultApplied { get; set; }

    /// <summary>
    /// Constancia de que la promoción del tope de pasadas ya corrió (F16 §D). Sin la marca correría
    /// en cada arranque y bajarlo a mano no sobreviviría a cerrar la aplicación, que es otra forma
    /// de tener el ajuste roto — la contraria.
    /// </summary>
    public bool SweepCapDefaultApplied { get; set; }

    /// <summary>
    /// Optional Copilot SDK BaseDirectory. Leave empty (default): the SDK uses its standard location,
    /// which is where the `copilot` CLI stores the login, so UseLoggedInUser finds it. Only set this
    /// if you deliberately want the SDK isolated to a custom directory.
    /// </summary>
    public string? CopilotBaseDirectory { get; set; }

    /// <summary>
    /// Max minutes to wait for the agent to finish auditing ONE unit before timing out (§5.1).
    /// The SDK default is 1 minute, which is too short for a real audit. Default here: 15.
    /// </summary>
    public int CopilotTimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Tope de pasadas del barrido por unidad (F4.1), editable en Ajustes desde F5.1.
    /// <para>
    /// Vive aquí, en la configuración de la máquina, y NO en <c>app.json</c>: el barrido gasta los
    /// tokens del asiento de quien lanza la sesión, así que es una preferencia del operador, no una
    /// propiedad de la app auditada. Un tope de 1 equivale a una pasada única, que es por lo que no
    /// hace falta ningún selector de «modo» por lanzamiento.
    /// </para>
    /// <para>
    /// <b>Por defecto 6 desde F16, y el número no es un ajuste a ojo.</b> El tope es un
    /// PRESUPUESTO —cuánto estoy dispuesto a pagar por unidad— y la regla de parada es otra cosa:
    /// desde F12 (D-755) el barrido necesita <b>dos pasadas secas seguidas</b> para declararse
    /// convergido, y esas dos salen del mismo presupuesto. Con un tope de 5, eso deja <b>3</b>
    /// pasadas que pueden aportar algo; antes de D-755, cuando bastaba una seca, dejaba 4. El tope
    /// se quedó en 5 cuando el criterio se hizo más estricto, y con eso el barrido se quedó sin
    /// margen: en el banco, una unidad gastó la 5ª añadiendo ubicaciones y otra llegó a la 5ª con
    /// su primera seca. Subir a 6 <b>restaura las 4 pasadas productivas</b> que la regla tenía
    /// antes de endurecerse. No es «un poco más»: es volver a la relación que había.
    /// </para>
    /// <para>
    /// El valor vigente se registra en cada sesión y en su informe, para que «cobertura
    /// posiblemente incompleta» siempre se pueda leer contra el tope que había.
    /// </para>
    /// </summary>
    public int MaxPassesPerUnit { get; set; } = SettingsLimits.DefaultMaxPassesPerUnit;

    /// <summary>
    /// <b>Modo exhaustivo</b> (R2 §1): cada pasada del barrido es una petición nueva con el prompt
    /// recompuesto, en vez de un turno de la conversación de la unidad.
    /// <para>
    /// <b>Apagado de fábrica</b>, y no es un tercer camino de código: es exactamente el camino de
    /// respaldo que F25 dejó puesto para cuando un hilo no puede continuar (D-922), con un
    /// interruptor delante. Encendido, la unidad no abre hilo y cada pasada viaja como viajaba antes
    /// de F25.
    /// </para>
    /// <para>
    /// <b>Lo que cuesta y lo que compra, medido</b> (M2, D-917/D-920): ×3 por unidad —204,7 credits
    /// contra 64,2 con la tarifa de Opus— y hallazgos duplicados que el hilo no produce (7 variantes
    /// marcadas contra 0); a cambio, 20,0 de los 20 defectos del caso de referencia contra 17,7. Dos
    /// defectos de gravedad media más por cada veinte.
    /// </para>
    /// <para>
    /// Vive aquí, en la configuración de la máquina, por lo mismo que el tope de pasadas: el barrido
    /// gasta los tokens del asiento de quien lanza la sesión, así que es una preferencia del
    /// operador y no una propiedad de la app auditada. Queda REGISTRADO en cada sesión, que es lo
    /// que permite a Métricas separar el coste de las dos formas de barrer.
    /// </para>
    /// <para>
    /// Se aplica a la SIGUIENTE sesión: cambiarlo a mitad de un barrido cambiaría la forma de
    /// auditar sin avisar, y la sesión ya escribió en su registro con cuál empezó.
    /// </para>
    /// </summary>
    public bool ExhaustiveSweep { get; set; }


    /// <summary>
    /// <b>El modelo con el que cada casa lanza sus sesiones, indexado por su identificador de
    /// proveedor</b> (PROV-2 §2). La lista de opciones se le pide al proveedor
    /// (<c>ListModelsAsync</c>), nunca se codifica a mano; esto solo guarda el id elegido.
    /// <para>
    /// <b>Un mapa y no dos campos, y ésa es la entrega.</b> Hasta PROV-2 había <c>copilotModel</c>
    /// y <c>claudeCodeModel</c>, y elegir entre ellos era un <c>if</c>: «si no es Claude Code, es
    /// el de Copilot». Con un tercer proveedor eso no es una simplificación, es un error — le
    /// entregaría el modelo de Copilot, que para él no significa nada. Lo que ya hubiera escrito
    /// con los nombres viejos lo adopta <see cref="SettingsService.AdoptLegacyProviderModels"/>,
    /// una vez, sin perderle el ajuste a nadie.
    /// </para>
    /// <para>
    /// <b>Sin entrada significa vacío, y vacío es importante que sea el valor por defecto</b>
    /// (F5.15). El modelo de Copilot nació aquí con <c>"gpt-5"</c> escrito a mano; el día que
    /// GitHub lo retiró, toda máquina con ajustes vírgenes nació rota. Un nombre de modelo es un
    /// dato del proveedor con fecha de caducidad y no puede vivir como constante: vacío significa
    /// «pregúntaselo al runtime», y de eso se encarga <c>ModelResolver</c> en el primer
    /// lanzamiento.
    /// </para>
    /// <para>
    /// <b>Y siguen siendo independientes por casa</b>, que era lo bueno de los dos campos: los
    /// espacios de nombres no se solapan —<c>gpt-5</c> no significa nada para Claude Code y
    /// <c>opus</c> no significa nada para Copilot—, así que ir y volver conserva las dos
    /// elecciones en vez de dejar una configurada con un id imposible.
    /// </para>
    /// </summary>
    /// <remarks>
    /// El comparador es insensible a mayúsculas y se REPONE en el <c>set</c>: lo que devuelve
    /// <c>JsonSerializer</c> es un diccionario ordinal recién creado, así que sin esto un
    /// identificador guardado con otra caja no se encontraría.
    /// </remarks>
    public Dictionary<string, string> ProviderModels
    {
        get => _providerModels;
        set => _providerModels = value is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> _providerOptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <b>Lo demás que un proveedor necesita de esta máquina</b> (PROV-3 §2), indexado por
    /// <c>{identificador}.{ajuste}</c>: la URL base de un endpoint, cómo presenta su clave.
    /// <para>
    /// Un saco y no un campo por cosa, por lo mismo que <see cref="ProviderModels"/>: una casa por
    /// API necesita datos que ni un proveedor de asiento ni uno de CLI necesitan, y un campo con el
    /// nombre de cada uno acabaría con un <c>AppSettings</c> que enumera las casas.
    /// </para>
    /// <para>
    /// <b>Aquí no cabe un secreto.</b> Esto es texto plano; la clave va a
    /// <see cref="ProviderSecretStore"/>, cifrada, y por eso la costura las pide por caminos
    /// distintos.
    /// </para>
    /// </summary>
    public Dictionary<string, string> ProviderOptions
    {
        get => _providerOptions;
        set => _providerOptions = value is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> _providerModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Con qué proveedor se lanzan las sesiones nuevas de ESTA máquina (F14): <c>copilot</c> o
    /// <c>claude-code</c>. Vacío = Copilot, que es el valor de fábrica y lo que tenían todas las
    /// máquinas antes de que hubiera un segundo.
    /// <para>
    /// <b>Personal, y por la regla de F13</b> (D-769, D-773): lo que decide no se escribe en el
    /// hub como política — se escribe como HECHO, en la sesión y en el informe («auditado con
    /// Claude Code, modelo X»). Cada uno audita con la suscripción que tiene, igual que ya elegía
    /// modelo y tope de pasadas, y lo que llega al hub no es el ajuste sino con quién se auditó
    /// aquella vez. Un proveedor compartido obligaría a que todo el equipo tuviera las mismas
    /// cuentas.
    /// </para>
    /// <para>
    /// Se aplica a la SIGUIENTE sesión: cambiarlo a mitad de un barrido cambiaría de juez sin
    /// avisar, y la sesión ya escribió en su registro con quién empezó.
    /// </para>
    /// </summary>
    public string AuditorProvider { get; set; } = string.Empty;

    // ---- Aviso de versión nueva (F8 §3) ----

    /// <summary>
    /// Cuándo se preguntó por última vez a GitHub por la última Release. Se comprueba en cada
    /// arranque, con un suelo anti-bucle de 15 minutos (<c>UpdateCheckService.MinimumInterval</c>):
    /// reiniciar tras publicar una release basta para ver el aviso, y abrir y cerrar diez veces
    /// seguidas no dispara diez consultas.
    /// <para>
    /// Solo se sella tras una consulta que SALIÓ BIEN. Si falla —sin red, sin permisos, API
    /// caída—, no se sella: quien arrancó offline no tiene por qué quedarse sin enterarse hasta
    /// que venza nada. Y no puede degenerar en machaqueo porque la consulta se hace una vez por
    /// arranque, no en bucle; el re-chequeo de las instancias que llevan abiertas cuenta desde su
    /// último INTENTO, y va cada 24 h.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// El tag de la última Release que se llegó a ver (<c>v1.2.3</c>). Se guarda para que el
    /// banner se pueda pintar en los arranques en los que el chequeo no toca: sin esto, la versión
    /// nueva desaparecería de la vista y volvería sola, que es justo el tipo de intermitencia que
    /// hace desconfiar de un aviso.
    /// </summary>
    public string? LastSeenReleaseTag { get; set; }

    /// <summary>La página de esa Release, para que «Ver novedades» funcione sin volver a preguntar.</summary>
    public string? LastSeenReleaseUrl { get; set; }

    /// <summary>
    /// La versión que el usuario descartó. No vuelve a molestar con ESA; con la siguiente sí.
    /// Descartar es «ya me he enterado», no «no me avises nunca más»: un interruptor permanente
    /// para un aviso que aparece una vez por versión sería más ajuste del que la función merece.
    /// </summary>
    public string? DismissedUpdateVersion { get; set; }
}

/// <summary>
/// Loads/saves <see cref="AppSettings"/>, protecting the PAT with Windows DPAPI (§3 — no
/// custom secret store). Reuses the git global identity as a fallback (§3).
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public SettingsService(AppPaths paths) => _path = paths.SettingsJson;

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions)
                          ?? new AppSettings();
            }
            catch
            {
                Current = new AppSettings();
            }
        }

        return Current;
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// Vuelve a los valores de fábrica: borra <c>settings.json</c> y deja
    /// <see cref="Current"/> en sus valores por defecto (F5.7 §5).
    /// <para>
    /// Las dos mitades importan. Borrar solo el fichero dejaría los ajustes viejos vivos en
    /// memoria, y el primer <see cref="Save"/> —lo hace hasta la migración de conexión— los
    /// volvería a escribir; poner solo <see cref="Current"/> a nuevo dejaría el fichero en disco
    /// para el siguiente arranque. Con el PAT pasa lo mismo: vive DENTRO de este fichero, así que
    /// borrarlo es también lo que se lleva la credencial de respaldo.
    /// </para>
    /// </summary>
    public void ResetToDefaults()
    {
        Current = new AppSettings();
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Best-effort: lo que manda es el estado en memoria, que ya es el de fábrica.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// One-time, silent migration of pre-F2 settings (D4). Users who already had a hub URL and a
    /// PAT must keep working untouched: their PAT stays (it is now the hidden fallback), and their
    /// hub URL is kept as an advanced override **only when it differs** from the deployment's —
    /// otherwise it is simply dropped, so those users follow the deployment from now on.
    /// </summary>
    public void MigrateConnection(DeployConfig deploy)
    {
        if (Current.ConnectionMigrated)
        {
            return;
        }

        string? legacy = Current.HubRepoUrl;
        if (!string.IsNullOrWhiteSpace(legacy)
            && !SameRepo(legacy, deploy.HubUrl)
            && string.IsNullOrWhiteSpace(Current.HubUrlOverride))
        {
            Current.HubUrlOverride = legacy!.Trim();
        }

        Current.HubRepoUrl = null;
        Current.ConnectionMigrated = true;
        Save(Current);
    }

    /// <summary>
    /// Promoción única del interruptor del arreglo asistido (F6.9, D-563).
    /// <para>
    /// El flag nació en v1 sin valor por defecto —es decir, <c>false</c>— conectado a nada
    /// (D-275), y <see cref="Save"/> escribe TODAS las propiedades: cualquier máquina que guardara
    /// ajustes antes de F6.9 tiene un <c>"enableAssistedFix": false</c> escrito con todas las
    /// letras. Cambiar el valor por defecto de la propiedad a <c>true</c> no alcanza a esas
    /// máquinas: el defecto solo se aplica cuando la clave FALTA, y ahí no falta. Por eso el
    /// botón «Arreglar con agente» no apareció el día de la entrega.
    /// </para>
    /// <para>
    /// Se promociona una sola vez y se deja constancia. A partir de esa vez, apagar el
    /// interruptor en Ajustes es una decisión del usuario y se respeta para siempre.
    /// </para>
    /// </summary>
    public void MigrateAssistedFixDefault()
    {
        if (Current.AssistedFixDefaultApplied)
        {
            return;
        }

        Current.AssistedFixDefaultApplied = true;
        Current.EnableAssistedFix = true;
        Save(Current);
    }

    /// <summary>
    /// Promoción única del tope de pasadas del barrido (F16 §D), con la misma forma que la de
    /// D-563 y por el mismo motivo: <see cref="Save"/> escribe TODAS las propiedades, así que las
    /// máquinas que ya han guardado ajustes traen un <c>"maxPassesPerUnit": 5</c> escrito con todas
    /// las letras y el valor por defecto nuevo no las alcanza — el defecto solo se aplica cuando la
    /// clave falta, y ahí no falta. Sin esto, el equipo entero seguiría barriendo con el tope viejo.
    /// <para>
    /// <b>Solo promociona el 5 exacto</b>, que es el valor de fábrica anterior. A quien haya
    /// elegido su propio número —3 para gastar menos, 10 para barrer a fondo— no se le toca: eso es
    /// una decisión, y pisarla sería exactamente lo que esta promoción existe para no hacer.
    /// </para>
    /// <para>
    /// <b>Y no es silenciosa</b> (D-765): devuelve la frase que la aplicación enseña una vez. Un
    /// presupuesto que sube solo y sin avisar es indistinguible de un ajuste que no ajusta.
    /// </para>
    /// </summary>
    /// <returns>Qué contarle al usuario, o <c>null</c> si no hubo nada que promocionar.</returns>
    public string? MigrateSweepCapDefault()
    {
        if (Current.SweepCapDefaultApplied)
        {
            return null;
        }

        Current.SweepCapDefaultApplied = true;

        if (Current.MaxPassesPerUnit != SettingsLimits.LegacyMaxPassesPerUnit)
        {
            Save(Current);
            return null;
        }

        Current.MaxPassesPerUnit = SettingsLimits.DefaultMaxPassesPerUnit;
        Save(Current);

        return $"El tope de pasadas del barrido ha pasado de {SettingsLimits.LegacyMaxPassesPerUnit} "
            + $"a {SettingsLimits.DefaultMaxPassesPerUnit}: el barrido necesita dos pasadas secas "
            + "seguidas para darse por terminado, y esas dos salen del mismo tope. Con 6 vuelve a "
            + "haber 4 pasadas que puedan aportar algo. Puedes cambiarlo en Ajustes → Auditoría.";
    }

    /// <summary>
    /// El ajuste que apuntaba a «Otro» pasa a «Manejador del sistema» (R13-2, D-1031).
    /// <para>
    /// «Otro (comando personalizado)» existió entre R13 y R13-2. Una máquina que lo tuviera elegido
    /// se quedaría con un ajuste que nombra una opción que ya no está: el desplegable no podría
    /// enseñarlo y abrir código no sabría qué lanzar. Se muda al manejador del sistema —que abre el
    /// fichero pase lo que pase— en vez de al editor de fábrica, porque es lo más parecido a «lo que
    /// tú tenías puesto» sin elegir por nadie.
    /// </para>
    /// <para>
    /// <b>Y no es silenciosa</b>, por la regla de D-765: devuelve la frase que la aplicación enseña
    /// una vez. Un ajuste que cambia solo y sin avisar es indistinguible de un ajuste que no ajusta.
    /// No hace falta bandera de «ya migrado»: en cuanto se escribe, el ajuste deja de decir
    /// «custom» y no hay nada que volver a migrar.
    /// </para>
    /// </summary>
    /// <returns>Qué contarle al usuario, o <c>null</c> si no había nada que mudar.</returns>
    public string? MigrateRetiredEditor()
    {
        if (!string.Equals(Current.Editor, RetiredCustomEditorId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Current.Editor = EditorRegistry.SystemId;
        Save(Current);

        return "El editor «Otro (comando personalizado)» se ha retirado: tu editor preferido pasa a "
            + "«Manejador del sistema». Puedes elegir otro en Ajustes → Avanzado.";
    }

    /// <summary>El id que tuvo «Otro» mientras existió. Se nombra para poder reconocerlo y mudarlo.</summary>
    internal const string RetiredCustomEditorId = "custom";

    /// <summary>Compares two remote URLs ignoring case, a trailing slash and a trailing ".git".</summary>
    internal static bool SameRepo(string? a, string? b)
    {
        static string Normalize(string? url) => (url ?? string.Empty)
            .Trim()
            .TrimEnd('/')
            .TrimEnd()
            is var u && u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? u[..^4].TrimEnd('/')
                : u;

        string na = Normalize(a);
        string nb = Normalize(b);
        return na.Length > 0 && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// El modelo configurado para UN proveedor (F14, PROV-2 §2). Es una <b>entrada del mapa</b>
    /// <see cref="AppSettings.ProviderModels"/>, buscada por el identificador que declara el
    /// proveedor; vacío significa «esta casa todavía no tiene ninguno elegido», y de eso se
    /// encarga <c>ModelResolver</c> al lanzar.
    /// <para>
    /// Aquí había un binario —«si no es Claude Code, es el de Copilot»— y por eso un tercer
    /// proveedor recibía el modelo de Copilot: un id que para él no significa nada, y que le
    /// rompería la primera sesión.
    /// </para>
    /// </summary>
    public string ModelFor(string? providerId)
    {
        string key = (providerId ?? string.Empty).Trim();
        return key.Length > 0 && Current.ProviderModels.TryGetValue(key, out string? model)
            ? model ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Guarda el modelo elegido para ese proveedor, sin tocar el de nadie más. Sin identificador
    /// no escribe: un modelo sin dueño acabaría siendo el de quien no lo eligió, que es
    /// exactamente el fallo del binario anterior.
    /// </summary>
    public void SetModelFor(string? providerId, string modelId)
    {
        string key = (providerId ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return;
        }

        AppSettings settings = Current;
        settings.ProviderModels[key] = (modelId ?? string.Empty).Trim();
        Save(settings);
    }

    /// <summary>
    /// <b>Adopta, una sola vez, el modelo que cada casa tenía guardado con su nombre viejo</b>
    /// (PROV-2 §2). Lo llama el arranque con los proveedores del registro, justo después de
    /// construirlo.
    /// <para>
    /// <b>Lo que no se puede perder es el ajuste de nadie.</b> Hasta esta entrega los modelos
    /// vivían en campos con nombre propio —<c>copilotModel</c>, <c>claudeCodeModel</c>— en el
    /// <c>settings.json</c> de cada máquina. Al pasar a un mapa por identificador, una
    /// actualización sin esto le borraría a todo el mundo el modelo que tenía elegido, en
    /// silencio: lo notaría en la siguiente auditoría, ya lanzada.
    /// </para>
    /// <para>
    /// <b>Cada casa dice con qué nombre guardaba el suyo</b> (<c>LegacyModelSettingKey</c>), y la
    /// clave se busca en el JSON crudo: ni reflexión ni una tabla de nombres aquí, que sería
    /// volver a repartir el conocimiento del proveedor por la aplicación. Quien no declare nombre
    /// viejo no tiene nada que adoptar, que es lo que le pasa a una casa nueva.
    /// </para>
    /// <para>
    /// <b>Es idempotente por doble motivo</b>: una entrada que ya está en el mapa no se pisa —el
    /// usuario pudo elegir otro desde entonces—, y al guardar, las claves viejas dejan de
    /// escribirse. Un valor vacío no crea entrada: vacío sigue siendo vacío.
    /// </para>
    /// </summary>
    /// <returns>Si hubo algo que adoptar, y por tanto se escribió.</returns>
    /// <summary>
    /// Un ajuste de ese proveedor, o null si no está puesto (PROV-3 §2). La clave del mapa la
    /// compone esta función —<c>{identificador}.{ajuste}</c>— para que nadie de fuera tenga que
    /// saber cómo se guarda.
    /// </summary>
    public string? ProviderOption(string providerId, string key)
    {
        string k = OptionKey(providerId, key);
        return k.Length > 0 && Current.ProviderOptions.TryGetValue(k, out string? value)
               && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    /// <summary>Guarda —o borra, con vacío— un ajuste de ese proveedor.</summary>
    public void SetProviderOption(string providerId, string key, string? value)
    {
        string k = OptionKey(providerId, key);
        if (k.Length == 0)
        {
            return;
        }

        AppSettings settings = Current;
        if (string.IsNullOrWhiteSpace(value))
        {
            settings.ProviderOptions.Remove(k);
        }
        else
        {
            settings.ProviderOptions[k] = value.Trim();
        }

        Save(settings);
    }

    private static string OptionKey(string providerId, string key)
        => string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(key)
            ? string.Empty
            : providerId.Trim() + "." + key.Trim();

    public bool AdoptLegacyProviderModels(IEnumerable<IAuditorProvider> providers)
    {
        IReadOnlyDictionary<string, string> legacy = ReadRawStrings();
        if (legacy.Count == 0)
        {
            return false;
        }

        AppSettings settings = Current;
        bool changed = false;
        foreach (IAuditorProvider provider in providers)
        {
            string key = (provider.LegacyModelSettingKey ?? string.Empty).Trim();
            string id = provider.ProviderId.Trim();
            if (key.Length == 0 || id.Length == 0 || settings.ProviderModels.ContainsKey(id))
            {
                continue;
            }

            if (legacy.TryGetValue(key, out string? model) && !string.IsNullOrWhiteSpace(model))
            {
                settings.ProviderModels[id] = model.Trim();
                changed = true;
            }
        }

        if (changed)
        {
            Save(settings);
        }

        return changed;
    }

    /// <summary>
    /// Las claves de texto de primer nivel del <c>settings.json</c> tal cual están en el fichero.
    /// Es la única forma de leer un ajuste cuyo nombre ya no es una propiedad de
    /// <see cref="AppSettings"/> — que es justo el caso de los que se adoptan.
    /// </summary>
    private IReadOnlyDictionary<string, string> ReadRawStrings()
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path))
        {
            return raw;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return raw;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    raw[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // Un fichero ilegible ya lo trata Load() volviendo a los valores de fábrica; aquí no
            // hay nada que adoptar y tampoco nada que romper.
        }

        return raw;
    }

    /// <summary>Encrypts and stores a PAT with DPAPI (current-user scope).</summary>
    public void SetPat(string? plainTextPat)
    {
        if (string.IsNullOrEmpty(plainTextPat))
        {
            Current.ProtectedPat = null;
        }
        else
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainTextPat), null, DataProtectionScope.CurrentUser);
            Current.ProtectedPat = Convert.ToBase64String(cipher);
        }

        Save(Current);
    }

    /// <summary>Decrypts the stored PAT, or null if none / undecryptable.</summary>
    public string? GetPat()
    {
        if (string.IsNullOrEmpty(Current.ProtectedPat))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(Current.ProtectedPat), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
