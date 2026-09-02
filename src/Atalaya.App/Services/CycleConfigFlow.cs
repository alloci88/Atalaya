using Atalaya.Agents;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// El flujo de «Configurar ciclo» (F17 §4), compartido por los tres momentos en que aparece —el
/// alta, el cierre y el panel (también el reinicio)—: monta el view-model con la lista de modelos
/// del proveedor preferente de quien configura, abre el diálogo y devuelve la configuración elegida,
/// o <c>null</c> si se canceló. Quien lo llama decide qué hacer con el <c>null</c>: el alta sigue
/// con los valores por defecto, el panel no toca nada.
/// <para>
/// La lista de modelos se pide al proveedor con un tope de espera. Si no contesta —sin red, sin
/// asiento— el combo trae el modelo que esta máquina tiene configurado, marcado como no verificado,
/// y una frase que dice por qué: un diálogo que se queda esperando a un SDK no es un diálogo.
/// </para>
/// </summary>
public sealed class CycleConfigFlow
{
    private static readonly TimeSpan ModelsTimeout = TimeSpan.FromSeconds(10);

    private readonly ICycleConfigDialog _dialog;
    private readonly AuditorProviderRegistry? _providers;
    private readonly SettingsService _settings;

    public CycleConfigFlow(ICycleConfigDialog dialog, SettingsService settings, AuditorProviderRegistry? providers = null)
    {
        _dialog = dialog;
        _settings = settings;
        _providers = providers;
    }

    /// <summary>Abre el diálogo para ese ciclo. Null = cancelado.</summary>
    public async Task<CycleConfig?> AskAsync(CycleConfigPreview preview, CycleConfigReason reason, CancellationToken ct = default)
    {
        IAuditorProvider? provider = _providers?.Current;
        string? providerId = provider?.ProviderId;
        string providerName = provider?.ProviderName ?? "sin proveedor";
        string configured = _settings.ModelFor(providerId).Trim();

        (IReadOnlyList<ModelOption> models, string notice) = await ListModelsAsync(provider, configured, ct);

        var vm = new CycleConfigViewModel();
        vm.Load(preview, reason, providerId, providerName, models, configured, notice);
        return _dialog.Show(vm) ? vm.Result : null;
    }

    private static async Task<(IReadOnlyList<ModelOption>, string)> ListModelsAsync(
        IAuditorProvider? provider, string configured, CancellationToken ct)
    {
        IReadOnlyList<ModelOption> fallback = configured.Length > 0
            ? new[] { ModelOption.Unverified(configured) }
            : Array.Empty<ModelOption>();

        if (provider is null)
        {
            return (fallback, string.Empty);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ModelsTimeout);
            IReadOnlyList<AgentModel> available = await provider.ListModelsAsync(timeout.Token);
            var options = available
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(ModelOption.From)
                .ToList();
            if (options.Count == 0)
            {
                return (fallback, $"Tu cuenta no ofrece ningún modelo de {provider.ProviderName} ahora mismo.");
            }

            // El configurado en esta máquina se ofrece aunque el proveedor ya no lo liste: lo que
            // se elige aquí es una preferencia del equipo, y ocultarlo lo haría desaparecer del
            // combo sin decir por qué.
            if (configured.Length > 0
                && !options.Any(o => string.Equals(o.Id, configured, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(ModelOption.Unverified(configured));
            }

            return (options, string.Empty);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (fallback, $"{provider.ProviderName} no contestó a tiempo: se ofrece solo el modelo configurado en esta máquina.");
        }
        catch (Exception ex)
        {
            string why = provider.Diagnose(ex).Message;
            return (fallback, $"No se pudo consultar la lista de modelos: {why} Se ofrece solo el configurado en esta máquina.");
        }
    }
}
