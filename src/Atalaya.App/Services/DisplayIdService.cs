using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Reparte el alias legible de un hallazgo (<c>BUG-0042</c>) — F5.6 §3, D-227/D-228.
/// <para>
/// <b>Por qué existe este fichero.</b> `DisplayId.Next` y `Finding.AssignDisplayId` llevaban
/// escritos desde el §2 y <b>no los llamaba nadie</b>: cero llamadas en toda la solución. Por eso
/// los 25 hallazgos del hub real tenían <c>"displayId": null</c> y el contador de la aplicación
/// seguía a cero después de decenas de pushes. No era un fallo de persistencia ni de lectura: el
/// paso simplemente no se había cableado nunca.
/// </para>
/// <para>
/// <b>Cuándo se reparte.</b> Tras un push con éxito, que es la regla del §2 contra colisiones
/// concurrentes: si dos máquinas numeran a la vez, la que pierde el push renumera un alias que
/// todavía no había publicado. Y al arrancar, como backfill de todo lo que quedó sin alias
/// mientras el paso no existía.
/// </para>
/// <para>
/// <b>Idempotente por construcción.</b> Solo mira los hallazgos con <c>DisplayId == null</c>, así
/// que la segunda pasada reparte cero y no toca ni el contador ni un solo fichero.
/// </para>
/// </summary>
public sealed class DisplayIdService
{
    private readonly HubContext _hub;

    public DisplayIdService(HubContext hub) => _hub = hub;

    /// <summary>Reparte alias en todas las aplicaciones del hub. Devuelve cuántos asignó.</summary>
    public int BackfillAll()
    {
        int total = 0;
        foreach (string slug in _hub.Store.ListAppSlugs())
        {
            total += AssignPending(slug);
        }

        return total;
    }

    /// <summary>
    /// Reparte alias a los hallazgos de <paramref name="slug"/> que no lo tienen, en orden de ULID
    /// —que es orden de creación, así que la numeración sigue la historia real de la aplicación—.
    /// Devuelve cuántos asignó; <c>0</c> si no había ninguno pendiente.
    /// </summary>
    public int AssignPending(string slug)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            return 0;
        }

        List<Finding> pending = _hub.Store.ListFindings(slug)
            .Where(f => string.IsNullOrEmpty(f.DisplayId))
            .OrderBy(f => f.Id.ToString(), StringComparer.Ordinal)
            .ToList();

        if (pending.Count == 0)
        {
            return 0;
        }

        var assigned = new List<(Finding Finding, string Alias)>(pending.Count);
        foreach (Finding f in pending)
        {
            assigned.Add((f, DisplayId.Next(app.DisplayIdCounters, f.Pillar)));
        }

        // El contador se guarda ANTES que los hallazgos: reserva los números. Si algo revienta a
        // mitad, lo que sobrevive es un contador adelantado y unos cuantos hallazgos sin alias —
        // que la próxima pasada numera de nuevo, sin repetir. Al revés se repartirían alias
        // duplicados.
        _hub.Store.WriteApp(app);

        foreach ((Finding f, string alias) in assigned)
        {
            f.AssignDisplayId(alias);
            _hub.Store.WriteFinding(slug, f);
        }

        return pending.Count;
    }
}
