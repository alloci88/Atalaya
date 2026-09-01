using Atalaya.ClaudeCode;
using Atalaya.Copilot;

namespace Atalaya.App.Services;

/// <summary>
/// Cómo se nombra una CASA a partir de lo que quedó escrito en el hub (F16 §C).
/// <para>
/// <b>Por qué hace falta esto además del registro.</b> <see cref="AuditorProviderRegistry.NameOf"/>
/// pregunta a los proveedores vivos, y para elegir con quién auditar eso es lo correcto. Pero los
/// informes y las métricas leen sesiones de hace meses desde sitios que no tienen —ni deben tener—
/// un registro delante: un <c>ReportBuilder</c> estático, una fila de una tabla. Aquí está la
/// traducción, y los identificadores salen de las constantes de cada driver, así que sigue
/// habiendo un solo sitio donde vive cada id.
/// </para>
/// <para>
/// <b>Un proveedor vacío es Copilot, y no «desconocido».</b> Las sesiones anteriores a F14 no
/// escribían la casa porque no había otra. Tratarlas como un dato que falta partiría el histórico
/// en dos justo en los hubs con más historia (D-780).
/// </para>
/// <para>
/// <b>Y un identificador que esta versión ya no trae se escribe tal cual.</b> Si algún día se
/// retira un proveedor, lo que está en el hub tiene que seguir pudiéndose leer en vez de salir en
/// blanco.
/// </para>
/// </summary>
public static class ProviderNames
{
    /// <summary>Cómo se llama para una persona.</summary>
    public static string Display(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return "GitHub Copilot";
        }

        string id = providerId!.Trim();
        return id.ToLowerInvariant() switch
        {
            RealCopilotAgent.Id => "GitHub Copilot",
            ClaudeCodeProvider.Id => "Claude Code",
            _ => id,
        };
    }

    /// <summary>
    /// El coste de esta casa es un EQUIVALENTE y no una factura (F15, D-789): con suscripción no se
    /// paga por tokens. Vive aquí, con el resto de lo que se sabe de cada casa leyendo el hub.
    /// </summary>
    public static bool IsSubscription(string? providerId)
        => string.Equals(providerId?.Trim(), ClaudeCodeProvider.Id, StringComparison.OrdinalIgnoreCase);
}
