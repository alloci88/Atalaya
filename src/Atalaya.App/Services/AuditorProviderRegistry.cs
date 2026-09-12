using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Quiénes pueden auditar en esta máquina, y cuál manda ahora (F14).
/// <para>
/// <b>Por qué existe.</b> Antes había UN agente y el contenedor lo inyectaba directamente. Con dos,
/// la pregunta «¿quién audita?» tiene respuesta variable —sale de Ajustes, y cambia sin reiniciar—
/// y alguien tiene que contestarla en el momento de lanzar. Si en vez de esto se inyectara el
/// proveedor elegido, los servicios SINGLETON (la sesión en vivo, el resolutor de modelo, la
/// pantalla Cuenta) se quedarían con el que hubiera al arrancar la aplicación: exactamente el fallo
/// de BUGFIX-AJUSTES, donde un valor capturado en el constructor hacía que cambiar el ajuste no
/// sirviera de nada hasta reiniciar.
/// </para>
/// <para>
/// Por eso <see cref="Current"/> es una propiedad que RELEE los ajustes cada vez, y no un campo.
/// </para>
/// </summary>
public sealed class AuditorProviderRegistry : IAsyncDisposable
{
    private readonly IReadOnlyList<IAuditorProvider> _providers;
    private readonly SettingsService? _settings;

    /// <param name="settings">
    /// De dónde sale la elección. Admite <c>null</c>, y eso significa «no hay nada que elegir»:
    /// es el caso de un registro de un solo proveedor, que es como lo montan los tests que
    /// ejercitan el pipeline con un agente falso. Sin ajustes, <see cref="Current"/> es el primero.
    /// </param>
    public AuditorProviderRegistry(SettingsService? settings, IEnumerable<IAuditorProvider> providers)
    {
        _settings = settings;
        _providers = providers.ToList();
        if (_providers.Count == 0)
        {
            throw new ArgumentException("Atalaya necesita al menos un proveedor de auditoría.", nameof(providers));
        }
    }

    /// <summary>Un registro de UNO. El atajo de los tests y de los caminos que no eligen.</summary>
    public static AuditorProviderRegistry Of(IAuditorProvider provider)
        => new(null, new[] { provider });

    /// <summary>
    /// Todos los proveedores, en el orden en que se registraron. Es el orden en el que la pantalla
    /// Cuenta los enseña y en el que Ajustes los ofrece, así que el primero es el de fábrica.
    /// </summary>
    public IReadOnlyList<IAuditorProvider> All => _providers;

    /// <summary>
    /// A quién se cae cuando el ajuste no nombra a nadie conocido: el <b>de fábrica</b>, que es
    /// quien lo declara (PROV-2 §2). Una máquina con un ajuste corrupto o con el nombre de un
    /// proveedor retirado audita, no se queda sin poder auditar.
    /// <para>
    /// Antes se buscaba por el tipo concreto de una casa, y eso ataba el registro —y con él la
    /// aplicación entera— al proyecto de ese proveedor. Ahora lo dice el contrato: si nadie se
    /// declara de fábrica, manda el orden de registro, que es el que ve el usuario.
    /// </para>
    /// </summary>
    public IAuditorProvider Fallback
        => _providers.FirstOrDefault(p => p.IsFactoryDefault) ?? _providers[0];

    /// <summary>
    /// <b>Quién escribió esto que ya está en el hub</b> (PROV-2 §2), o <c>null</c> si esta versión
    /// ya no trae esa casa.
    /// <para>
    /// Sin identificador escrito es de quien reclame el histórico sin atribuir —antes de F14 no
    /// había otra casa, así que no se escribía ninguna (revisa D-780)—, y quien lo reclama lo
    /// declara en el contrato en vez de comparar una cadena contra su nombre.
    /// </para>
    /// </summary>
    public IAuditorProvider? WhoWrote(string? providerId)
        => string.IsNullOrWhiteSpace(providerId)
            ? _providers.FirstOrDefault(p => p.ClaimsUnattributedSessions)
            : _providers.FirstOrDefault(p =>
                  string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cómo contaba sus tokens de entrada la casa que escribió esa sesión (PROV-2 §2). De una que
    /// esta versión ya no trae se supone la forma del de fábrica, que es la del histórico.
    /// </summary>
    public TokenAccounting AccountingOf(string? providerId)
        => (WhoWrote(providerId) ?? Fallback).Accounting;

    /// <summary>
    /// Cómo factura y en qué se enseña lo que gastó esa casa (PROV-2 §3). De una retirada no se
    /// inventa unidad: dólares, que es la unidad del dominio.
    /// </summary>
    public ProviderBilling BillingOf(string? providerId)
        => WhoWrote(providerId)?.Billing ?? ProviderBilling.Default;

    /// <summary>
    /// El proveedor con el que se lanzaría una sesión AHORA. Se resuelve en cada lectura: cambiar
    /// el proveedor en Ajustes surte efecto en la siguiente sesión sin reiniciar la aplicación.
    /// <para>
    /// Si el elegido es OPCIONAL y ya no está en la máquina —lo desinstalaron, cambió el PATH—, se
    /// vuelve al de fábrica (F14, adenda). Es la regla de «ninguna merma» aplicada al peor momento
    /// posible: un ajuste guardado hace semanas no puede dejar a nadie sin poder auditar hoy.
    /// </para>
    /// </summary>
    public IAuditorProvider Current
    {
        get
        {
            IAuditorProvider chosen = ById(_settings?.Current.AuditorProvider);
            return chosen.IsOptional && !chosen.IsPresent ? Fallback : chosen;
        }
    }

    /// <summary>
    /// Quién ARREGLA ahora (F16). Es el proveedor activo <b>si sabe arreglar</b>, y nada si no.
    /// <para>
    /// <b>No cae a otro, y ésa es la decisión.</b> Sería fácil buscar el primero de la lista que
    /// implemente el arreglo asistido, y sería mentir: la pantalla anuncia con quién se arregla, y
    /// arreglar con una casa distinta de la que se anunció convierte el proveedor en un dato que no
    /// se puede creer. Cuando no hay quien arregle, la sesión no arranca y lo dice — que es lo
    /// mismo que hace cuando el proveedor elegido no está autenticado.
    /// </para>
    /// <para>
    /// Hoy los dos proveedores de verdad arreglan, así que esto solo devuelve null con un tercero
    /// que no lo haga. Existe porque el tipo lo exige, y el tipo lo exige para que ese tercero no
    /// se cuele por accidente.
    /// </para>
    /// </summary>
    public IAssistedFixProvider? CurrentFixer => Current as IAssistedFixProvider;

    /// <summary>
    /// Los que se pueden elegir de verdad en esta máquina: los que no son opcionales —Copilot, que
    /// siempre está— más los opcionales que sí estén instalados. Es lo que ofrece Ajustes: un
    /// desplegable no debe ofrecer algo que no va a funcionar.
    /// </summary>
    public IReadOnlyList<IAuditorProvider> Selectable
        => _providers.Where(p => !p.IsOptional || p.IsPresent).ToList();

    /// <summary>
    /// El proveedor con ese identificador, o el <see cref="Fallback"/> si no se reconoce. Nunca
    /// devuelve null: quien pregunta va a auditar, y devolverle nada sería dejarle sin juez.
    /// </summary>
    public IAuditorProvider ById(string? providerId)
        => string.IsNullOrWhiteSpace(providerId)
            ? Fallback
            : _providers.FirstOrDefault(p =>
                  string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
              ?? Fallback;

    /// <summary>
    /// El nombre para mostrar de un identificador guardado, aunque ese proveedor ya no exista en
    /// esta versión. Los informes y las métricas leen sesiones de hace meses: si un día se retira
    /// un proveedor, lo que está escrito en el hub tiene que seguir pudiéndose nombrar en vez de
    /// salir en blanco.
    /// </summary>
    public string NameOf(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Fallback.ProviderName;
        }

        return _providers.FirstOrDefault(p =>
                   string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
               ?.ProviderName
               // F16: y si esta versión ya no lo trae, el mapa de lectura del histórico sabe
               // nombrarlo igual que lo nombran los informes. Un solo texto por casa.
               ?? ProviderNames.Display(providerId);
    }

    /// <summary>
    /// <b>Cierra a los proveedores que lo necesiten</b> (PROV-2 §1).
    /// <para>
    /// Hasta aquí cada proveedor se registraba por su cuenta en el contenedor, y era el contenedor
    /// quien los liberaba al cerrar. Desde que los monta <c>Atalaya.Providers</c>, el contenedor
    /// solo conoce ESTE objeto —los proveedores los creó otro—, así que el cierre tiene que bajar
    /// por aquí. Si no bajara, volvería BUGFIX-CIERRE: el runtime del proveedor se queda vivo con
    /// sus tuberías abiertas y <c>Atalaya.exe</c> no termina al cerrar la ventana (D-085, D-086).
    /// </para>
    /// <para>
    /// Se libera lo que se declare liberable y nada más: un doble de test no tiene por qué serlo.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (IAuditorProvider provider in _providers)
        {
            switch (provider)
            {
                case IAsyncDisposable async:
                    await async.DisposeAsync();
                    break;
                case IDisposable sync:
                    sync.Dispose();
                    break;
            }
        }
    }
}
