using System.Threading;

namespace Atalaya.App.Tests;

/// <summary>
/// Corre un trozo de código en un hilo STA propio, que es lo que WPF exige para MEDIR, DISPONER y
/// RENDERIZAR un árbol visual.
/// <para>
/// Existe para los dos únicos tests que necesitan píxeles de verdad: que el treemap coloque sus
/// celdas cuando se le da un tamaño, y que la exportación produzca un PNG. Todo lo demás se
/// comprueba sobre el view-model o sobre el XAML, que es más rápido y no depende del entorno
/// gráfico. Un runner de xUnit no garantiza un hilo STA, así que se crea uno y se espera.
/// </para>
/// </summary>
internal static class StaRunner
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("el trabajo en el hilo STA falló", failure);
        }
    }
}
