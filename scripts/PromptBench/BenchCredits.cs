namespace Atalaya.PromptBench;

/// <summary>
/// <b>Poner precio a los tokens del banco, para poder comparar dos brazos</b> (M2).
/// <para>
/// <b>Esto NO es el coste de una sesión y no puede leerse como tal.</b> Claude Code no factura a la
/// organización —<c>CreditCalculator.IsBilled("claude-code")</c> es <c>false</c> desde
/// F16-RETOQUE—, así que una sesión suya no tiene credits y la aplicación, con toda la razón, no se
/// los inventa. Lo que hace falta aquí es otra cosa: una <b>valoración</b> con una tarifa fija que
/// permita decir «este brazo cuesta la mitad que el otro» sin comparar cuatro columnas de tokens a
/// ojo.
/// </para>
/// <para>
/// <b>La tarifa es la de Opus publicada</b>, la misma con la que F20 reprodujo AL CREDIT la factura
/// de la sesión de referencia (D-871): 5 / 25 / 0,50 / 6,25 $ por millón de tokens de entrada
/// fresca, salida, lectura de caché y escritura de caché, y 1 credit = 0,01 $. Los números de aquel
/// reparto salen de estas cuatro cifras y de ninguna otra, y por eso son las que se usan: cambiarlas
/// haría incomparables las tablas de M2 con las de F20 y F21.
/// </para>
/// <para>
/// <b>Y la semántica de entrada es la de Claude Code</b> (<c>TokenAccounting.InputExcludesCache</c>):
/// lo que el CLI declara como <c>input_tokens</c> ya viene sin la caché, así que se tarifa entero y
/// no se resta nada. Con Copilot habría que restar, y por eso esta clase no es de uso general: es
/// del banco, y el banco solo mide Claude Code.
/// </para>
/// </summary>
internal static class BenchCredits
{
    private const decimal UsdPerCredit = 0.01m;

    private const decimal FreshPerMillion = 5m;
    private const decimal OutputPerMillion = 25m;
    private const decimal CacheReadPerMillion = 0.50m;
    private const decimal CacheWritePerMillion = 6.25m;

    /// <summary>Los cuatro conceptos, cada uno a su tarifa, y el total es su suma.</summary>
    public static decimal Opus(long fresh, long output, long cacheRead, long cacheWrite)
        => (PerMillion(fresh, FreshPerMillion)
            + PerMillion(output, OutputPerMillion)
            + PerMillion(cacheRead, CacheReadPerMillion)
            + PerMillion(cacheWrite, CacheWritePerMillion))
           / UsdPerCredit;

    private static decimal PerMillion(long tokens, decimal rate)
        => tokens <= 0 ? 0m : tokens / 1_000_000m * rate;
}
