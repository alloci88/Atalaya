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

    /// <summary>Preferred editor for "open in editor" (§8): "vs" or "vscode".</summary>
    public string Editor { get; set; } = "vs";

    /// <summary>"dark" or "light".</summary>
    public string Theme { get; set; } = "dark";

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
    /// Modelo de Copilot con el que se lanzan las sesiones nuevas (<c>SessionConfig.Model</c>).
    /// La lista de opciones se pide al SDK (<c>ListModelsAsync</c>), nunca se codifica a mano; esto
    /// solo guarda el id elegido.
    /// <para>
    /// <b>Vacío por defecto, y es importante que lo sea (F5.15).</b> Aquí ponía <c>"gpt-5"</c>
    /// escrito a mano. El día que GitHub retiró ese modelo, toda máquina con ajustes vírgenes nació
    /// rota: la primera auditoría moría en <c>session.create</c>. Un nombre de modelo es un dato del
    /// proveedor con fecha de caducidad y no puede vivir como constante. Vacío significa
    /// «pregúntaselo al runtime», y de eso se encarga <c>ModelResolver</c> en el primer lanzamiento.
    /// </para>
    /// </summary>
    public string CopilotModel { get; set; } = string.Empty;

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

    /// <summary>
    /// Modelo con el que Claude Code lanza las sesiones (<c>claude --model</c>).
    /// <para>
    /// <b>Es un campo aparte de <see cref="CopilotModel"/> a propósito.</b> Los dos espacios de
    /// nombres no se solapan —<c>gpt-5</c> no significa nada para Claude Code y <c>opus</c> no
    /// significa nada para Copilot—, así que compartir el campo garantizaría que cambiar de
    /// proveedor dejara configurado un modelo imposible. Con uno cada uno, ir y volver conserva
    /// las dos elecciones.
    /// </para>
    /// <para>
    /// Vacío por defecto, por la misma razón que el de Copilot (F5.15): un nombre de modelo es un
    /// dato del proveedor con fecha de caducidad y no puede vivir como constante. Vacío significa
    /// «que elija el CLI», y de eso se encarga <c>ModelResolver</c>.
    /// </para>
    /// </summary>
    public string ClaudeCodeModel { get; set; } = string.Empty;

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
    /// El modelo configurado para UN proveedor (F14). Cada casa tiene su campo porque sus espacios
    /// de nombres no se solapan; esto es el único sitio que sabe cuál es cuál, para que el
    /// resolutor de modelo y Ajustes no tengan que repetir el <c>switch</c>.
    /// </summary>
    public string ModelFor(string? providerId)
        => IsClaudeCode(providerId) ? Current.ClaudeCodeModel : Current.CopilotModel;

    /// <summary>Guarda el modelo elegido para ese proveedor, sin tocar el del otro.</summary>
    public void SetModelFor(string? providerId, string modelId)
    {
        AppSettings settings = Current;
        if (IsClaudeCode(providerId))
        {
            settings.ClaudeCodeModel = modelId;
        }
        else
        {
            settings.CopilotModel = modelId;
        }

        Save(settings);
    }

    private static bool IsClaudeCode(string? providerId)
        => string.Equals(providerId, "claude-code", StringComparison.OrdinalIgnoreCase);

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
