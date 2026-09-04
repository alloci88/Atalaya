using Atalaya.App.Services;

namespace Atalaya.App.ViewModels;

/// <summary>
/// El portafolio ENTERO en cinco números (F26 Parte B, D-972).
/// <para>
/// <b>Por qué existe.</b> El portafolio enseñaba una tarjeta por aplicación y nada más, así que la
/// pregunta con la que se abre Atalaya por la mañana —«¿cuánto hay abierto, y de qué gravedad?»—
/// había que contestarla sumando tarjetas a ojo. Con una aplicación se podía; con quince, no. Y el
/// sitio donde ponerlo estaba libre: era el 60 % de pantalla en blanco de la queja.
/// </para>
/// <para>
/// <b>Por qué se deriva y no se consulta.</b> Es la misma cuenta que ya hacen las tarjetas, vista
/// desde más lejos. Una consulta propia podría dar OTRO número —por un filtro distinto, por un
/// caché, por un error— y entonces habría dos verdades en la misma pantalla. Sumando las tarjetas,
/// si el resumen está mal es que las tarjetas están mal.
/// </para>
/// </summary>
public sealed record PortfolioSummary(int Apps, int Critica, int Alta, int Media, int Baja)
{
    public static PortfolioSummary Empty { get; } = new(0, 0, 0, 0, 0);

    public static PortfolioSummary Of(IEnumerable<AppCard> cards)
    {
        int apps = 0, critica = 0, alta = 0, media = 0, baja = 0;

        foreach (AppCard card in cards)
        {
            apps++;
            critica += card.Critica;
            alta += card.Alta;
            media += card.Media;
            baja += card.Baja;
        }

        return new PortfolioSummary(apps, critica, alta, media, baja);
    }

    public int Total => Critica + Alta + Media + Baja;

    /// <summary>
    /// La línea bajo el título. Dice lo que hay, en singular o en plural — un «1 aplicaciones» en
    /// la primera línea de la aplicación es de las cosas que se quedan grabadas.
    /// </summary>
    public string Headline => Apps switch
    {
        0 => "Ninguna aplicación registrada todavía",
        1 => $"1 aplicación · {Findings}",
        _ => $"{Apps} aplicaciones · {Findings}",
    };

    private string Findings => Total switch
    {
        0 => "sin hallazgos abiertos",
        1 => "1 hallazgo abierto",
        _ => $"{Total} hallazgos abiertos",
    };
}
