using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// La marca en pantalla: <b>la organización configurada, o nada</b> (F38 §1).
/// <para>
/// <b>Qué era y por qué cambia.</b> Hasta F38 esto resolvía un logotipo corporativo en disco,
/// elegía variante según el tema y le ponía una placa clara debajo cuando hacía falta. La
/// aplicación ha salido de aquella organización, así que el logotipo ya no es de nadie: lo que
/// identifica un despliegue es el nombre que declara su hub, y ése es texto.
/// </para>
/// <para>
/// <b>Sin organización, el hueco desaparece.</b> Ni marco vacío, ni interrogante, ni «—»: un
/// despliegue sin organización es una situación normal, no un error (D-464, con otro contenido).
/// </para>
/// </summary>
public partial class BrandMark : UserControl
{
    public static readonly DependencyProperty OrganizationProperty = DependencyProperty.Register(
        nameof(Organization), typeof(string), typeof(BrandMark), new PropertyMetadata(null, OnOrganizationChanged));

    /// <summary>
    /// El nombre de la organización, tal y como lo da el hub. Nulo o en blanco significa «este
    /// despliegue no declara ninguna», que es el caso por defecto desde que
    /// <c>appsettings.deploy.json</c> sale sin organización.
    /// </summary>
    public string? Organization
    {
        get => (string?)GetValue(OrganizationProperty);
        set => SetValue(OrganizationProperty, value);
    }

    public BrandMark()
    {
        InitializeComponent();
        Refresh();
    }

    private static void OnOrganizationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BrandMark)d).Refresh();

    /// <summary>
    /// El nombre se recorta —el hub puede traerlo con espacios— y, si no queda nada, el control
    /// se colapsa entero: no basta con dejar el texto vacío, porque el hueco seguiría ocupando su
    /// sitio en la fila que lo contiene.
    /// </summary>
    private void Refresh()
    {
        string name = Organization?.Trim() ?? string.Empty;
        OrgText.Text = name;
        Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
