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
    /// <b>Si lo que se busca es si esa casa FACTURA, se pregunta en otro sitio</b>:
    /// <see cref="CreditCalculator.IsBilled"/> (F16-RETOQUE §1). Aquí vivió un reenvío durante un
    /// rato y se quitó — dos puertas a la misma decisión son dos sitios donde mirar cuando alguien
    /// quiera cambiarla, y esta clase es la de los NOMBRES.
    /// </summary>
}
