using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>La divisa es un ajuste ESTÁTICO, y dos tests no pueden cambiarla a la vez</b> (R6 §8).
/// <para>
/// <c>CostFormat.Currency</c> es estática a propósito, como la cultura y el tema: la lee todo lo
/// que escribe un coste, y pasarla de mano en mano por docenas de firmas acabaría con una firma
/// que no la recibe (F29 §2). El precio lo pagan los tests: en paralelo, la clase que la pone en
/// dólares para comprobar la gráfica se la cambia también a la que está comprobando que el diálogo
/// de lanzamiento dice «credits» — y el rojo sale una vez de cada diez ejecuciones, en un test que
/// no tiene nada que ver. Pasó a la primera con dos clases tocándola.
/// </para>
/// <para>
/// Quien la cambie entra en ESTA colección, que corre <b>sola</b>: nada más se ejecuta mientras
/// tanto. El resto del conjunto sigue en paralelo, que es lo que lo mantiene en menos de un
/// minuto.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrencyCollection
{
    public const string Name = "divisa";
}

/// <summary>
/// <b>Y quien monta una <see cref="System.Windows.Application"/> corre solo</b> (R6 §1).
/// <para>
/// WPF admite UNA por dominio y con afinidad de hilo, así que una prueba que la crea para resolver
/// recursos <c>pack://</c> —la única forma de cargar los diccionarios como los carga la
/// aplicación— no puede convivir con otra que lea <c>Application.Current</c> desde su propio hilo:
/// lo que sale de ahí no es un rojo, es un bloqueo, y el conjunto se queda colgado.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppCollection
{
    public const string Name = "aplicación";
}
