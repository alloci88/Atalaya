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
/// <b>El aire lo pone la cabecera</b> (F16-RETOQUE §2). Que las dos zonas no se pisen dejó de ser
/// suficiente en cuanto se vio la pantalla: quedaban pegadas, y «pegado» se lee como «apretado»
/// aunque cada píxel esté en su sitio. El hueco entre identidad y acciones (<see cref="ZoneGap"/>)
/// lo escribe esta clase en el margen de la zona de identidad, no cada vista — el mismo argumento
/// que el reparto de columnas: una separación declarada en dos XAML acaba siendo dos separaciones
/// distintas. <b>Las vistas no ponen margen a sus dos zonas</b>; si lo pusieran, se perdería.
/// </para>
/// <para>
/// <b>Y a anchos estrechos baja a dos filas</b> (F16-RETOQUE §2·5). Comprimir es la peor salida:
/// la identidad se recorta hasta no decir nada y los botones se aprietan hasta tocarse. Cuando a la
/// identidad no le quedaría ni <see cref="MinIdentityWidth"/> px, la cabecera se reordena —
/// identidad arriba, acciones debajo, con <see cref="RowGap"/> entre medias— y las dos zonas
/// vuelven a caber enteras. Se decide con el ancho que piden las ACCIONES, que es el dato duro:
/// son las que no ceden.
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
/// columnas —y el aire entre ellas— los pone la propia cabecera, así que las vistas no los
/// declaran: declararlos sería volver a tener el reparto escrito en dos sitios, que es de donde
/// viene todo esto.
/// </remarks>
public sealed class PageHeader : Grid
{
    /// <summary>
    /// El aire entre la zona de identidad y la de acciones. No es decoración: es lo que separa
    /// «reparte» de «se tocan», y lo que hace que el ojo lea dos bloques y no una fila apretada.
    /// </summary>
    public const double ZoneGap = 16;

    /// <summary>El aire entre las dos filas cuando la cabecera se parte por falta de ancho.</summary>
    public const double RowGap = 12;

    /// <summary>
    /// Lo mínimo que se le deja a la identidad antes de partir la cabecera en dos filas. Por debajo
    /// de esto no cabe ni el título de la página, así que recortar dejaría de informar de nada.
    /// </summary>
    public const double MinIdentityWidth = 200;

    public PageHeader() => ClipToBounds = true;

    /// <summary>
    /// Reparte los hijos —por columnas, o por filas cuando no hay ancho— antes de medir. Se hace
    /// aquí y no en el constructor porque en XAML los hijos llegan después de construirse el panel,
    /// y se rehace en cada pasada porque el ancho disponible cambia con la ventana.
    /// <para>
    /// El ÚLTIMO hijo son las acciones; todo lo anterior es identidad. Con los dos hijos de siempre
    /// eso es «el primero a la izquierda y el segundo a la derecha»; con uno solo, ese uno es
    /// identidad y la derecha queda vacía — que es lo que quiere una página sin acciones.
    /// </para>
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        int last = InternalChildren.Count - 1;
        var identity = last >= 0 ? InternalChildren[0] as FrameworkElement : null;
        var actions = last > 0 ? InternalChildren[last] as FrameworkElement : null;

        // La identidad se RECORTA a su celda, y las acciones no. Es la garantía dura: por muy bien
        // repartidas que estén las columnas, un panel anidado que no quepa dibuja fuera de la suya
        // sin pedirle permiso a nadie, y lo que se pinta encima de un botón se pulsa igual. Se pone
        // aquí, en el dueño del reparto, y no en cada vista — que es donde se olvida.
        for (int i = 0; i < last; i++)
        {
            if (InternalChildren[i] is UIElement zone)
            {
                zone.ClipToBounds = true;
            }
        }

        Stacked = actions is not null && !FitsInOneRow(actions, constraint.Width);
        PlaceZones(identity, actions);

        return base.MeasureOverride(constraint);
    }

    /// <summary>La cabecera está partida en dos filas. Lo consultan los tests de geometría.</summary>
    public bool Stacked { get; private set; }

    /// <summary>
    /// ¿Caben las dos zonas en una fila? Se mide lo que piden las ACCIONES —que es lo que no cede—
    /// y se comprueba que a la identidad le queda un mínimo habitable. Medir la identidad no
    /// serviría: vive en una columna estrella y siempre pide más de lo que necesita.
    /// </summary>
    private static bool FitsInOneRow(FrameworkElement actions, double available)
    {
        if (double.IsInfinity(available) || double.IsNaN(available))
        {
            return true;
        }

        actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return available - actions.DesiredSize.Width - ZoneGap >= MinIdentityWidth;
    }

    private void PlaceZones(FrameworkElement? identity, FrameworkElement? actions)
    {
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        if (Stacked)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        if (identity is not null)
        {
            SetRow(identity, 0);
            SetColumn(identity, 0);

            // El hueco entre las dos zonas, puesto donde no se puede olvidar. En una fila va al
            // costado de la identidad; en dos, debajo. Nunca en las acciones: el borde derecho de
            // la cabecera es la referencia del ojo y meterle margen la descuadraría.
            identity.Margin = Stacked
                ? new Thickness(0, 0, 0, RowGap)
                : new Thickness(0, 0, ZoneGap, 0);
        }

        if (actions is not null && actions != identity)
        {
            SetRow(actions, Stacked ? 1 : 0);
            SetColumn(actions, Stacked ? 0 : 1);

            // Partida en dos filas, las acciones se alinean a la IZQUIERDA: quedan bajo la
            // identidad, como una segunda línea de lo mismo, y no colgando del borde opuesto.
            actions.HorizontalAlignment = Stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
    }
}
