namespace Atalaya.App.Services;

/// <summary>
/// Cómo se nombra una CASA a partir de lo que quedó escrito en el hub (F16 §C, PROV-2 §2).
/// <para>
/// <b>Por qué hace falta esto además del registro.</b> <see cref="AuditorProviderRegistry.NameOf"/>
/// pregunta a los proveedores vivos, y para elegir con quién auditar eso es lo correcto. Pero los
/// informes y las métricas leen sesiones de hace meses desde sitios que no tienen —ni deben tener—
/// un registro delante: un <c>ReportBuilder</c> estático, una fila de una tabla.
/// </para>
/// <para>
/// <b>Lo que cambió en PROV-2.</b> Aquí había un <c>switch</c> con el identificador y el nombre de
/// las dos casas escritos a mano, y era el foco que PROV-1 midió: una línea que se propaga a
/// quince sitios de presentación —informes, métricas, fichas, ciclos—. Ahora no nombra a nadie:
/// el mapa se SIEMBRA desde el registro al arrancar, y cada casa dice cómo se llama. Añadir una
/// tercera no toca este fichero.
/// </para>
/// <para>
/// <b>El arranque es el único escritor</b>, y sin sembrar el valor por defecto es el identificador
/// tal cual — nunca el nombre de una casa. Poner aquí un nombre de fábrica «por si acaso» sería
/// volver a meter el literal que esto viene a quitar, y además mentiría en la única situación en
/// que se nota: un registro que no traiga esa casa.
/// </para>
/// <para>
/// <b>Y un identificador que esta versión ya no trae se escribe tal cual.</b> Si algún día se
/// retira un proveedor, lo que está en el hub tiene que seguir pudiéndose leer en vez de salir en
/// blanco. Por eso el mapa no es un diccionario cerrado: lo que no está sale como está escrito.
/// </para>
/// <para>
/// <b>Un proveedor vacío es de quien reclame el histórico sin atribuir</b> (D-780). Las sesiones
/// anteriores a F14 no escribían la casa porque no había otra; quién las reclama lo declara el
/// contrato (<c>ClaimsUnattributedSessions</c>), no un <c>if</c> con su nombre.
/// </para>
/// </summary>
public static class ProviderNames
{
    private static IReadOnlyDictionary<string, string> _byId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cómo se llama quien reclama el histórico sin atribuir. Vacío si nadie lo reclama.</summary>
    private static string _unattributed = string.Empty;

    /// <summary>
    /// Siembra el mapa con los proveedores registrados. <b>Lo llama el arranque, una vez</b>, justo
    /// después de construir el registro; nadie más escribe aquí. Volver a llamarlo reemplaza el
    /// mapa entero, que es lo que hace falta para que un arnés de pruebas pueda montar el suyo.
    /// </summary>
    public static void Seed(IEnumerable<IAuditorProvider> providers)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string unattributed = string.Empty;
        foreach (IAuditorProvider provider in providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.ProviderId))
            {
                names[provider.ProviderId.Trim()] = provider.ProviderName;
            }

            if (provider.ClaimsUnattributedSessions && unattributed.Length == 0)
            {
                unattributed = provider.ProviderName;
            }
        }

        _byId = names;
        _unattributed = unattributed;
    }

    /// <summary>Cómo se llama para una persona.</summary>
    public static string Display(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return _unattributed;
        }

        string id = providerId!.Trim();
        return _byId.TryGetValue(id, out string? name) ? name : id;
    }

    /// <summary>
    /// <b>Si lo que se busca es si esa casa tiene precio, no se pregunta a nadie</b>: desde PROV-2
    /// §3 lo contesta la tabla de tarifas —hay importe si hay tarifa para ese proveedor y ese
    /// modelo—, y la frase de cuando no la hay la declara el propio proveedor en su
    /// <c>Billing</c>. Aquí vivió un reenvío a la vieja decisión por cadena y se quitó: dos
    /// puertas a la misma pregunta son dos sitios donde mirar, y esta clase es la de los NOMBRES.
    /// </summary>
}
