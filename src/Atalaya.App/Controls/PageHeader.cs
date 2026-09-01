using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// La cabecera de una página: identidad a la izquierda, acciones a la derecha, <b>y nunca las dos
/// en el mismo sitio</b>.
/// <para>
/// <b>El defecto que la trae.</b> Las cabeceras eran un <c>Grid</c> sin columnas con dos
/// <c>StackPanel</c> dentro: uno normal y otro con <c>HorizontalAlignment="Right"</c>. Eso no
/// reparte espacio — <b>superpone</b>, exactamente igual que dos hijos en la misma fila (la lección
/// de D-710b, en el otro eje). Mientras la izquierda fue corta no se notó; en cuanto el arreglo
/// asistido ganó el distintivo de proveedor y modelo (F16), «Pausar» se pintó encima de «Volver al
/// hallazgo (MEJ-0011)».
/// </para>
/// <para>
/// <b>Y estaba en las dos vistas</b>, como el banner de fallo: la sesión en vivo tenía la misma
/// forma y el mismo riesgo, solo que con menos piezas a la izquierda. Por eso esto es una clase y
/// no un retoque en un XAML: el patrón se arregla donde vive.
/// </para>
/// <para>
/// <b>Cómo reparte.</b> Dos columnas: la izquierda <c>*</c> —se queda con lo que sobre y encoge
/// hasta cero— y la derecha <c>Auto</c> —mide lo que necesitan sus botones y no cede nunca—. Las
/// acciones son lo que no se puede perder: un botón medio tapado se pulsa igual y hace lo que sea
/// que haya debajo. La identidad, en cambio, se recorta y se lee en el tooltip.
/// </para>
/// <para>
/// <b>Y recorta lo que se salga</b> (<see cref="UIElement.ClipToBounds"/>). Es el cinturón además
/// de los tirantes: un <c>StackPanel</c> anidado que no quepa dibuja fuera de su columna sin
/// pedirle permiso a nadie, así que se le pone el límite aquí, donde no depende de que cada vista
/// se acuerde.
/// </para>
/// </summary>
/// <remarks>
/// Se usa con exactamente dos hijos y en este orden: <b>identidad</b> y <b>acciones</b>. Las
/// columnas las pone la propia cabecera, así que las vistas no las declaran — declararlas sería
/// volver a tener el reparto escrito en dos sitios, que es de donde viene todo esto.
/// </remarks>
public sealed class PageHeader : Grid
{
    public PageHeader()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ClipToBounds = true;
    }

    /// <summary>
    /// Reparte los hijos por columnas antes de medir. Se hace aquí y no en el constructor porque en
    /// XAML los hijos llegan después de construirse el panel, y se hace en cada pasada porque es
    /// idempotente y cuesta lo que un par de asignaciones: más barato que suscribirse a cambios de
    /// la colección y tener dos caminos que mantener.
    /// <para>
    /// El ÚLTIMO hijo son las acciones; todo lo anterior es identidad. Con los dos hijos de siempre
    /// eso es «el primero a la izquierda y el segundo a la derecha»; con uno solo, ese uno es
    /// identidad y la derecha queda vacía — que es lo que quiere una página sin acciones.
    /// </para>
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        int last = InternalChildren.Count - 1;
        for (int i = 0; i <= last; i++)
        {
            if (InternalChildren[i] is not UIElement child)
            {
                continue;
            }

            bool actions = i == last && last > 0;
            SetColumn(child, actions ? 1 : 0);

            // La identidad se RECORTA a su columna, y las acciones no. Es la garantía dura: por
            // muy bien repartidas que estén las columnas, un panel anidado que no quepa dibuja
            // fuera de la suya sin pedirle permiso a nadie, y lo que se pinta encima de un botón
            // se pulsa igual. Se pone aquí, en el dueño del reparto, y no en cada vista — que es
            // donde se olvida.
            if (child is UIElement zone && !actions)
            {
                zone.ClipToBounds = true;
            }
        }

        return base.MeasureOverride(constraint);
    }
}
