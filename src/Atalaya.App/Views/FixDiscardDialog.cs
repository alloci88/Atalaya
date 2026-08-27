using System.Windows;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

/// <summary>
/// La confirmación de «Descartar todo» (F6.9 §4). Es un <c>MessageBox</c> y no una ventana propia
/// a propósito: la pregunta cabe en tres frases, no tiene ningún campo que rellenar y no hay nada
/// que teclear para desbloquearla —lo que se pierde es el trabajo del AGENTE de esta sesión, no
/// trabajo del usuario: el árbol estaba limpio al empezar, que es justo lo que hace seguro este
/// botón—. Una ventana con estilo propio para esto sería ceremonia sin contenido.
/// </summary>
public sealed class FixDiscardDialogConfirmer : IFixDiscardConfirmer
{
    /// <summary>Cuántos ficheros se nombran antes de resumir.</summary>
    public const int MaxListed = 10;

    public bool Confirm(IReadOnlyList<string> files)
    {
        string list = files.Count == 0
            ? "No hay ningún fichero registrado."
            : string.Join(Environment.NewLine, files.Take(MaxListed).Select(f => $"  · {f}"))
              + (files.Count > MaxListed ? $"{Environment.NewLine}  …y {files.Count - MaxListed} más" : string.Empty);

        MessageBoxResult answer = MessageBox.Show(
            $"Se van a revertir {files.Count} fichero(s) a como estaban antes de este arreglo:"
            + Environment.NewLine + Environment.NewLine + list
            + Environment.NewLine + Environment.NewLine
            + "Lo que el agente haya escrito se pierde. Tu trabajo no: el clon estaba limpio al "
            + "empezar la sesión." + Environment.NewLine + Environment.NewLine
            + "¿Descartar los cambios?",
            "Descartar el arreglo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }
}
