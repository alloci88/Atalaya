using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// La tabla de tarifas de la organización, leída del hub (F15).
/// <para>
/// <b>Se relee del disco en cada consulta y no se cachea en memoria.</b> El hub se sincroniza por
/// detrás mientras la aplicación está abierta, así que una copia guardada al arrancar significaría
/// que quien corrija un precio en su máquina no cambia lo que ve el de al lado hasta que reinicie.
/// Es la misma razón por la que el proveedor elegido se relee (D-776) y por la que el modelo se lee
/// en cada sesión (BUGFIX-AJUSTES). Leer un JSON pequeño es barato; equivocarse de precio, no.
/// </para>
/// </summary>
public sealed class ModelRatesService
{
    private readonly HubContext _hub;

    public ModelRatesService(HubContext hub) => _hub = hub;

    /// <summary>
    /// La tabla vigente, o null si la organización todavía no la ha sembrado. Devolver null y no
    /// una tabla vacía es deliberado: «no hay tabla» tiene un remedio —sembrarla— y «hay tabla sin
    /// esta tarifa» tiene otro, y quien pregunta necesita poder distinguirlos.
    /// </summary>
    public ModelRateTable? Current
    {
        get
        {
            try
            {
                // PROV-2 §3 — con la columna de proveedor adoptada, que es lo que hace el lector
                // del hub: las 31 tarifas que ya existen se sembraron sin ella y son la lista de
                // precios de la casa de fábrica. Nadie pierde su tabla y ningún precio cambia.
                return _hub.ModelRates();
            }
            catch (Exception)
            {
                // Un fichero corrupto o a medio escribir por un merge no puede tumbar Métricas.
                // Sin tabla, los costes salen como «tarifa no configurada», que es honesto.
                return null;
            }
        }
    }

    /// <summary>¿Hay tabla? Lo pregunta la pantalla, para decir por qué está vacía.</summary>
    public bool IsSeeded => Current is not null;

    /// <summary>
    /// La casa de fábrica, con la que nace una fila nueva de la tabla de tarifas (PROV-2 §3).
    /// Vacío sin registro, y entonces la fila pide el proveedor antes de dejarse guardar.
    /// </summary>
    public string FactoryProviderId => _hub.FactoryProviderId;

    /// <summary>
    /// <b>Las casas que esta máquina conoce</b> (R-PROV2), para el filtro de la tabla. Son las
    /// registradas; las que además tengan tarifas escritas las añade quien lee la tabla, porque en
    /// el hub puede haber la tarifa de una casa que esta versión ya no traiga y esconderla dejaría
    /// filas imposibles de encontrar.
    /// </summary>
    public IReadOnlyList<string> KnownProviders
        => _hub.Providers?.All.Select(p => p.ProviderId).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// Con qué identificador se busca la tarifa de la casa que escribió una sesión (PROV-2 §3).
    /// No es siempre el que la sesión guardó: las anteriores a F14 no guardaron ninguno.
    /// </summary>
    public ProviderCostTraits TraitsOf(string? providerId) => _hub.CostTraits(providerId);

    /// <summary>
    /// Escribe la tabla que le den <b>y la publica</b>. Quien llama es la pantalla de gestión, que
    /// ya validó; el esquema vuelve a validar al escribir, como todo lo que entra en el hub.
    /// <para>
    /// <b>El commit es explícito desde R2.</b> Hasta aquí guardar una tarifa solo tocaba el fichero:
    /// llegaba al hub cuando cualquier otra operación commiteaba —y como <c>Commit</c> añade TODO lo
    /// que haya cambiado, el precio corregido acababa viajando dentro de un commit de claims o de
    /// hallazgos. El commit del hub es la atribución de esta tabla (D-786), así que tiene que decir
    /// que lo que cambió fue una tarifa.
    /// </para>
    /// </summary>
    public void Save(ModelRateTable table)
    {
        _hub.Store.WriteModelRates(table);
        _hub.Sync?.CommitAndPush($"tarifas: {table.Rates.Count} tarifa(s) editadas a mano");
    }

    /// <summary>
    /// <b>Siembra lo que falte, sin pisar nada, y lo publica</b> (R2 §2). Devuelve cuántas filas
    /// se han escrito —sembradas de nuevo, o adoptando el proveedor de fábrica que les faltaba
    /// (PROV-2 §3)—; 0 significa que la tabla ya estaba al día y que no se ha tocado nada.
    /// <para>
    /// <b>Sembrar ya no es un gesto de nadie.</b> Esto era <c>EnsureSeeded</c> y lo llamaba la
    /// pantalla al abrirse: mientras nadie visitara Métricas → Tarifas · Gestionar, el hub no tenía
    /// tabla y TODAS las sesiones salían con «tarifa no configurada». Lo llama ahora la apertura del
    /// hub (<see cref="HubContext.SeedModelRates"/>), sin preguntar.
    /// </para>
    /// <para>
    /// <b>Lo editado manda sobre lo sembrado, siempre</b>, y el criterio es por MODELO: basta con que
    /// la tabla nombre ese modelo para que la siembra lo deje en paz. Así un hub que ya tenía tabla
    /// sí recibe los modelos que no conocía — que es lo que la regla por tabla de D-786 no podía
    /// hacer, y lo que dejaba a un modelo nuevo saliendo como «tarifa no configurada» para siempre.
    /// </para>
    /// <para>
    /// <b>Y no escribe cuando no hay nada que añadir.</b> El commit del hub es la atribución de esta
    /// tabla (D-786): un commit idéntico por arranque la convertiría en ilegible.
    /// </para>
    /// </summary>
    public int SeedMissing()
    {
        ModelRateTable? existing;
        try
        {
            // Se lee del ALMACÉN y no de `Current`, que se traga los errores y devuelve null: por ahí
            // un fichero corrupto se leería como «no hay tabla» y la siembra lo pisaría entero.
            existing = _hub.Store.TryReadModelRates();
        }
        catch (Exception)
        {
            // Un fichero corrupto o a medio escribir por un merge NO se siembra encima: eso borraría
            // lo que alguien tenga escrito ahí. Se deja como está, que es lo que hace que el problema
            // se vea y se arregle en el hub.
            return 0;
        }

        ModelRateSeed.SeedFill fill = ModelRateSeed.Fill(existing, _hub.FactoryProviderId);
        if (!fill.HasChanges)
        {
            return 0;
        }

        _hub.Store.WriteModelRates(fill.Table);
        _hub.Sync?.CommitAndPush(Commit(fill));
        return fill.Added.Count + fill.Adopted;
    }

    /// <summary>
    /// Qué dice el commit del hub, que es la atribución de esta tabla (D-786): lo sembrado, lo
    /// adoptado, o las dos cosas. Un mensaje que dijera «siembra» cuando lo único que pasó fue
    /// ponerle proveedor a lo que ya había haría ilegible el histórico de la tabla.
    /// </summary>
    private static string Commit(ModelRateSeed.SeedFill fill)
    {
        if (fill.Added.Count == 0)
        {
            return $"tarifas: {fill.Adopted} tarifa(s) adoptan el proveedor de fábrica";
        }

        string seeded = $"tarifas: siembra automática de {fill.Added.Count} tarifa(s) que faltaban";
        return fill.Adopted == 0
            ? seeded
            : $"{seeded}; {fill.Adopted} adoptan el proveedor de fábrica";
    }


    /// <summary>
    /// <b>Aquí vivió <c>Billable</c></b>, que escondía las tarifas de la casa que no facturaba
    /// (F16-RETOQUE §1). Se retira con <c>IsBilled</c> (PROV-2 §3): con la columna de proveedor,
    /// <b>una tarifa de cualquier casa es legítima</b>, y esconder la que alguien haya escrito era
    /// justo lo contrario de lo que hace falta ahora — el día que se le ponga precio a una casa,
    /// tiene que verse y poderse corregir como la de cualquier otra.
    /// </summary>
    /// <summary>
    /// El coste de una sesión con la tarifa de SU modelo. Es el único camino por el que la
    /// aplicación pone precio a unos tokens, para que no haya dos aritméticas.
    /// </summary>
    public CostResult CostOf(AuditSession session)
        => CostCalculator.Calculate(session, Current, null, _hub.CostTraits(session.Provider));

    /// <summary>
    /// Los modelos que APARECEN en las sesiones del hub y no tienen tarifa configurada (F15). Es lo
    /// que la pantalla de tarifas señala: sin esta lista, un modelo nuevo se traduce en agregados
    /// parciales sin que nadie sepa qué falta añadir.
    /// <para>
    /// <b>Solo de las casas a las que les FALTA una</b> (F16-RETOQUE §1, sin el <c>if</c> por
    /// nombre desde PROV-2 §3). A un modelo que solo se ha usado con una casa que declara qué se
    /// lee cuando no lleva precio no le falta ninguna tarifa: es que no lleva ninguna. Listarlo
    /// aquí pediría configurar un precio que esa casa dice que no existe, y quien lo configurara
    /// empezaría a ver un coste donde no lo hay. Lo declara ella; aquí no se compara ningún nombre.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Model, string? Provider, int Sessions)> ModelsWithoutRate()
    {
        ModelRateTable? table = Current;
        var missing = new Dictionary<string, (string Model, string? Provider, int Sessions)>(StringComparer.OrdinalIgnoreCase);

        foreach (string slug in _hub.Store.ListAppSlugs())
        {
            foreach (AuditSession session in _hub.Store.ListSessions(slug))
            {
                ProviderCostTraits traits = _hub.CostTraits(session.Provider);
                if (traits.NoRateNote is { Length: > 0 })
                {
                    continue;
                }

                if (session.Model is not { Length: > 0 } model)
                {
                    continue;
                }

                // F29 §0 — `auto` NO es un modelo sin tarifa: es el enrutador de Copilot, y no hay
                // ningún precio publicado para «lo que el enrutador decida». Listarlo aquí es lo que
                // hacía que la pantalla pidiera una tarifa que no debe existir —«auto (copilot) · 1
                // sesión»—, y que quien la añadiera empezara a ver un coste inventado. Esas sesiones
                // no se pierden: salen en la insignia de su aplicación, que es donde se reconcilian.
                if (ModelIds.IsPlaceholder(model))
                {
                    continue;
                }

                string? provider = traits.ProviderId ?? session.Provider;
                if (table?.Find(model, provider) is not null)
                {
                    continue;
                }

                string key = $"{provider} {model}";
                missing[key] = missing.TryGetValue(key, out var seen)
                    ? seen with { Sessions = seen.Sessions + 1 }
                    : (model, provider, 1);
            }
        }

        return missing.Values.OrderByDescending(m => m.Sessions).ThenBy(m => m.Model).ToList();
    }
}
