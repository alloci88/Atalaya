using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Lo que pasó al intentar fijar la política de tamaño: qué quedó guardado y qué hubo que
/// corregir. Las correcciones viajan con el resultado —y no se aplican en silencio— por lo mismo
/// que en Ajustes: un valor corregido sin decirlo se vive igual que un ajuste que no ajusta.
/// </summary>
/// <param name="Saved">Se pudo escribir. False solo si la aplicación ya no está en el hub.</param>
/// <param name="Published">El cambio salió hacia el remoto. False cuando no hay sync configurado.</param>
public sealed record ThresholdPolicyResult(
    bool Saved,
    int LargeUnitLoc,
    int LargeUnitChars,
    IReadOnlyList<string> Corrections,
    bool Published)
{
    public static ThresholdPolicyResult NotFound { get; } =
        new(false, 0, 0, Array.Empty<string>(), false);

    /// <summary>La frase del toast: lo que quedó, y lo que hubo que corregir para que quedara.</summary>
    public string Message => !Saved
        ? "La aplicación ya no está en el hub."
        : Corrections.Count == 0
            ? $"Umbral de unidad grande: {LargeUnitLoc} LOC / {LargeUnitChars} caracteres. "
              + "Aplica en el próximo re-escaneo."
            : $"Umbral guardado con correcciones — {string.Join(" · ", Corrections)}. "
              + $"Queda en {LargeUnitLoc} LOC / {LargeUnitChars} caracteres.";
}

/// <summary>
/// La política de tamaño de una aplicación (F13): a partir de cuántas líneas —o de cuántos
/// caracteres— una unidad es demasiado grande para auditarla de una vez.
/// <para>
/// <b>Por qué es política y no preferencia.</b> Lo que escribe estado compartido se gobierna con
/// ajuste compartido. Este umbral clasifica el inventario y crea (o resuelve) hallazgos de tamaño,
/// y las dos cosas viven en el hub: con el umbral en la máquina de cada uno, dos compañeros con
/// números distintos se pisaban en cada re-escaneo — la clasificación y sus hallazgos iban y venían
/// sin que nadie hubiera decidido nada. Aquí hay una sola verdad, y el commit del hub dice quién la
/// cambió y cuándo.
/// </para>
/// <para>
/// Vive junto al presupuesto de directivas (D-712) y por la misma razón: las dos son propiedades de
/// la aplicación auditada, no de quien la audita esta tarde.
/// </para>
/// </summary>
public sealed class ThresholdPolicyService
{
    private readonly HubContext _hub;

    public ThresholdPolicyService(HubContext hub) => _hub = hub;

    /// <summary>
    /// La política vigente. Sin app —o sin hub— devuelve la de fábrica: es lo que estrenaría, y
    /// contestar con ceros haría que la pantalla enseñara un umbral que no existe.
    /// </summary>
    public Thresholds Read(string slug)
        => _hub.Store.TryReadApp(slug)?.Thresholds ?? new Thresholds();

    /// <summary>
    /// Fija la política y la publica. Los mínimos son los mismos de Ajustes
    /// (<see cref="SettingsLimits"/>): un umbral de 0 marcaría «grande» hasta un fichero vacío.
    /// </summary>
    public ThresholdPolicyResult Set(string slug, int largeUnitLoc, int largeUnitChars)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            return ThresholdPolicyResult.NotFound;
        }

        var corrections = new List<string>();
        int loc = Floor(largeUnitLoc, SettingsLimits.MinLargeUnitLoc, "el umbral por líneas", "LOC", corrections);
        int chars = Floor(
            largeUnitChars, SettingsLimits.MinLargeUnitChars, "el umbral por peso", "caracteres", corrections);

        app.Thresholds.LargeUnitLoc = loc;
        app.Thresholds.LargeUnitChars = chars;
        _hub.Store.WriteApp(app);

        // Un cambio de política que se queda en la máquina de quien lo hizo es exactamente el
        // problema que esto viene a resolver, así que se publica en el mismo gesto.
        bool published = _hub.Sync?.CommitAndPush(
            $"policy: umbral de unidad grande de {slug} = {loc} LOC / {chars} caracteres") ?? false;

        return new ThresholdPolicyResult(true, loc, chars, corrections, published);
    }

    private static int Floor(int value, int minimum, string what, string unit, List<string> corrections)
    {
        int applied = SettingsLimits.Clamp(value, minimum, out bool corrected);
        if (corrected)
        {
            corrections.Add($"{what}: el mínimo es {minimum} {unit}");
        }

        return applied;
    }
}
