using System.Windows;
using System.Windows.Controls;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>
    /// <b>Alguien ha escrito de verdad en la caja de la clave</b> (PROV-3 §3).
    /// <para>
    /// Sin esta bandera, tabular por encima de una caja que nace vacía —y nace vacía siempre,
    /// porque la clave guardada no se enseña— guardaría una clave vacía al salir, es decir,
    /// <b>borraría la que hay</b> sin que nadie la haya tocado. Con ella, vaciar el campo sigue
    /// siendo la forma de borrarla, pero hay que hacerlo a propósito.
    /// </para>
    /// </summary>
    private bool _secretTyped;

    public SettingsView() => InitializeComponent();

    private void ClaveApi_PasswordChanged(object sender, RoutedEventArgs e) => _secretTyped = true;

    /// <summary>
    /// La clave se guarda al salir del campo, como el resto de cajas de esta página, y la caja se
    /// vacía justo después: lo que está guardado lo dice la línea de debajo, y el secreto no se
    /// queda en el árbol visual esperando a una captura de pantalla.
    /// </summary>
    private void ClaveApi_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_secretTyped || sender is not Wpf.Ui.Controls.PasswordBox box)
        {
            return;
        }

        string typed = box.Password;
        box.Password = string.Empty;
        _secretTyped = false;

        (DataContext as SettingsViewModel)?.SaveApiKey(typed);
    }
}
