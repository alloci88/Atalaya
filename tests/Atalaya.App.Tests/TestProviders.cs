using Atalaya.Agents;
using Atalaya.App.Services;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain.Model;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>Las dos casas de verdad, para lo que DECLARAN del coste</b> (PROV-2 §3).
/// <para>
/// Desde que el coste se tarifa por proveedor + modelo, un test del coste necesita saber lo que
/// declara la casa que gastó: con qué identificador se busca su tarifa, cómo cuenta sus tokens de
/// entrada y qué se lee cuando no lleva precio. Se lo pregunta a las casas de verdad y no a una
/// copia escrita aquí — una copia se queda desfasada en silencio, y entonces el test seguiría
/// verde mientras la aplicación dice otra cosa.
/// </para>
/// <para>
/// Construirlas es barato: ninguna de las dos toca la red, el disco ni una credencial hasta que se
/// le pide auditar.
/// </para>
/// </summary>
public static class TestProviders
{
    /// <summary>La casa de fábrica: la que factura a la organización, en AI credits.</summary>
    public static readonly IAuditorProvider Copilot = new RealCopilotAgent();

    /// <summary>La opcional: la que declara que su consumo no lleva tarifa nuestra.</summary>
    public static readonly IAuditorProvider Claude = new ClaudeCodeProvider("puente-de-prueba.exe");

    /// <summary>Las dos, en el orden en que las registra la aplicación.</summary>
    public static AuditorProviderRegistry Registry() => new(null, new[] { Copilot, Claude });

    /// <summary>Los rasgos de coste de la casa de fábrica.</summary>
    public static ProviderCostTraits CopilotTraits => Copilot.CostTraits();

    /// <summary>Los de la opcional, con su frase de «sin tarifa».</summary>
    public static ProviderCostTraits ClaudeTraits => Claude.CostTraits();

    /// <summary>Lo que la casa de fábrica dice que no lleva precio. No hay ninguna.</summary>
    public static string ClaudeNote => Claude.Billing.NoRateNote!;

    /// <summary>La lente con la que se escribe el gasto de la casa de fábrica: sus AI credits.</summary>
    public static CostLens CopilotLens => CostLens.Recorded(Copilot.Billing);

    /// <summary>
    /// <b>Lo que hace el arranque de la aplicación, para los tests</b> (PROV-2 §3): decirle al
    /// formateador de qué casa es la moneda que la preferencia de esta máquina pide. Sin esto
    /// —igual que sin el arranque— todo se escribiría en dólares, que es lo que se supone de una
    /// casa que no declara moneda propia, y los tests medirían otra aplicación.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void ReferenceHouse() => CostFormat.Billing = Copilot.Billing;
}
