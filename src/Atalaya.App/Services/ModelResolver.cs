using Atalaya.Copilot;

namespace Atalaya.App.Services;

/// <summary>
/// Qué modelo se va a usar y por qué (F5.15).
/// </summary>
/// <param name="ModelId">
/// El id que debe llevar la sesión. Vacío significa «no se pudo decidir»: ver <paramref name="Failed"/>.
/// </param>
/// <param name="Notice">
/// Qué contarle al usuario, si hay algo que contar. Null cuando el modelo configurado seguía siendo
/// válido — el caso normal, que no merece un aviso.
/// </param>
/// <param name="Changed">El ajuste guardado cambió como consecuencia de esta resolución.</param>
/// <param name="Failed">
/// No hay ningún modelo utilizable. La sesión no debe arrancar: hacerlo solo consigue que el
/// runtime la rechace más tarde y con peor mensaje.
/// </param>
public sealed record ModelResolution(string ModelId, string? Notice, bool Changed, bool Failed = false)
{
    public static ModelResolution Keep(string modelId) => new(modelId, null, false);
}

/// <summary>
/// Decide con qué modelo se lanza una sesión, preguntándoselo al runtime en vez de creerse un
/// literal (F5.15).
/// <para>
/// <b>Por qué existe.</b> <c>AppSettings.CopilotModel</c> nacía con <c>"gpt-5"</c> escrito a mano.
/// El día que GitHub retiró ese modelo, toda máquina con ajustes vírgenes nació rota: la primera
/// auditoría moría en <c>session.create</c> con «Model gpt-5 is not available». Un nombre de modelo
/// es un dato del proveedor con fecha de caducidad, y no puede vivir como constante en el código de
/// nadie. El valor por defecto pasa a ser <b>vacío</b> = «pregúntaselo al runtime».
/// </para>
/// <para>
/// <b>Cómo elige.</b> El primero que el runtime lista. Es el único criterio que no caduca: cualquier
/// preferencia por nombre («los gpt antes que los claude», «evita los mini») vuelve a meter
/// literales que envejecen igual que el que causó el parte. El orden que devuelve
/// <c>ListModelsAsync</c> es el del propio proveedor para esa cuenta, y si no acierta, la elección
/// se enseña y se cambia en Ajustes con dos clics.
/// </para>
/// <para>
/// <b>Nunca falla en silencio.</b> Cada camino devuelve o un modelo o un <see cref="ModelResolution.Failed"/>
/// con su frase; el que llama está obligado a enseñarla.
/// </para>
/// </summary>
public sealed class ModelResolver
{
    private readonly ICopilotAgent _agent;
    private readonly SettingsService _settings;

    public ModelResolver(ICopilotAgent agent, SettingsService settings)
    {
        _agent = agent;
        _settings = settings;
    }

    /// <summary>
    /// Resuelve el modelo para la próxima sesión y, si hubo que cambiarlo, lo GUARDA: la siguiente
    /// auditoría no puede volver a tropezar con lo mismo, ni el usuario tener que arreglarlo dos
    /// veces.
    /// </summary>
    public async Task<ModelResolution> ResolveAsync(CancellationToken ct)
    {
        string configured = (_settings.Current.CopilotModel ?? string.Empty).Trim();

        IReadOnlyList<AgentModel> available;
        try
        {
            available = await _agent.ListModelsAsync(ct);
        }
        catch (Exception ex)
        {
            // No se pudo preguntar. Con un modelo configurado se sigue adelante con él —puede ser
            // perfectamente válido y el fallo estar en la red—; sin ninguno no hay nada que probar.
            return configured.Length > 0
                ? ModelResolution.Keep(configured)
                : new ModelResolution(
                    string.Empty,
                    "No se pudo consultar la lista de modelos de tu cuenta ("
                    + Short(ex.Message) + "), y esta máquina todavía no tiene ninguno elegido. "
                    + "Abre Ajustes y elige uno.",
                    Changed: false,
                    Failed: true);
        }

        var usable = available.Where(m => !string.IsNullOrWhiteSpace(m.Id)).ToList();
        if (usable.Count == 0)
        {
            return new ModelResolution(
                configured,
                "Tu cuenta no ofrece ningún modelo de Copilot. Revisa tu asiento antes de auditar.",
                Changed: false,
                Failed: configured.Length == 0);
        }

        if (configured.Length > 0 && usable.Any(m => string.Equals(m.Id, configured, StringComparison.OrdinalIgnoreCase)))
        {
            return ModelResolution.Keep(configured);   // el caso normal: ni aviso ni escritura
        }

        AgentModel chosen = usable[0];
        Save(chosen.Id);

        return new ModelResolution(
            chosen.Id,
            configured.Length == 0
                ? $"Modelo elegido automáticamente: {Describe(chosen)}. Puedes cambiarlo en Ajustes."
                : $"El modelo «{configured}» ya no está disponible para tu cuenta; "
                  + $"se ha cambiado a {Describe(chosen)}. Puedes elegir otro en Ajustes.",
            Changed: true);
    }

    /// <summary>El nombre legible con su id cuando difieren: el id es lo que hay que reconocer en Ajustes.</summary>
    private static string Describe(AgentModel model)
        => string.IsNullOrWhiteSpace(model.Name) || model.Name == model.Id
            ? model.Id
            : $"{model.Name} ({model.Id})";

    private static string Short(string? message)
        => string.IsNullOrWhiteSpace(message) ? "sin detalle"
            : message!.Length <= 120 ? message : message[..120] + "…";

    private void Save(string modelId)
    {
        AppSettings s = _settings.Current;
        s.CopilotModel = modelId;
        _settings.Save(s);
    }
}
