using Atalaya.Copilot;

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
public sealed class AuditorProviderRegistry
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
    /// A quién se cae cuando el ajuste no nombra a nadie conocido: Copilot, que es con quien
    /// funcionaba todo antes de F14. Una máquina con un ajuste corrupto o con el nombre de un
    /// proveedor retirado audita, no se queda sin poder auditar.
    /// </summary>
    public IAuditorProvider Fallback
        => _providers.FirstOrDefault(p => p.ProviderId == RealCopilotAgent.Id) ?? _providers[0];

    /// <summary>
    /// El proveedor con el que se lanzaría una sesión AHORA. Se resuelve en cada lectura: cambiar
    /// el proveedor en Ajustes surte efecto en la siguiente sesión sin reiniciar la aplicación.
    /// </summary>
    public IAuditorProvider Current => ById(_settings?.Current.AuditorProvider);

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
               ?? providerId!;
    }
}
