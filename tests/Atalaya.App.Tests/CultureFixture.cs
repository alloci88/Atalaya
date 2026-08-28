using System.Globalization;
using System.Runtime.CompilerServices;
using Atalaya.App.Services;

namespace Atalaya.App.Tests;

/// <summary>
/// Pone los tests en la MISMA cultura en la que corre la aplicación (F8.1).
/// <para>
/// <b>Por qué existe.</b> El estreno del release de F8 falló por un solo test: esperaba un coste
/// «67,5» y en el runner de GitHub —cultura invariante— salió «67.5». El test no estaba mal: lo
/// que estaba mal es que asumía la cultura de la máquina en vez de fijarla. Un test que solo pasa
/// en los portátiles del equipo no prueba lo que dice probar, y el runner es el único sitio donde
/// eso se ve.
/// </para>
/// <para>
/// <b>Un inicializador de módulo y no un fixture de xUnit</b> porque tiene que estar puesto
/// ANTES de que corra nada, incluidos los constructores de las clases de test y cualquier estado
/// estático que se inicialice de camino. Un <c>ICollectionFixture</c> llega tarde para eso, y
/// obligaría a que cada clase se acordara de pedirlo — que es exactamente la clase de disciplina
/// que este arreglo viene a quitar.
/// </para>
/// <para>
/// <b>Ojo con lo que esto NO autoriza.</b> Fijar la cultura aquí hace deterministas los tests de
/// interfaz, pero no prueba que los ARTEFACTOS COMPARTIDOS sean independientes de la máquina: si
/// un informe se apoyara en la cultura ambiente, este fixture lo taparía. Por eso los informes
/// fijan su cultura a mano y se comprueban desde una cultura hostil — ver
/// <see cref="ReportCultureTests"/>.
/// </para>
/// </summary>
internal static class CultureFixture
{
    [ModuleInitializer]
    internal static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = AppCulture.Display;
        CultureInfo.DefaultThreadCurrentUICulture = AppCulture.Display;
        CultureInfo.CurrentCulture = AppCulture.Display;
        CultureInfo.CurrentUICulture = AppCulture.Display;
    }
}
