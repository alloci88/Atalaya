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

/// <summary>
/// La pregunta de «Cerrar» cuando el agente dejó ficheros tocados (BUGFIX-CIERRE).
/// <para>
/// Son TRES respuestas y por eso no es un sí/no: conservar (lo normal — el árbol es del usuario),
/// descartar (que es el otro gesto, el de siempre) y cancelar. El botón por defecto es conservar:
/// cerrar una pantalla no puede tocar el árbol de trabajo de nadie por inercia.
/// </para>
/// </summary>
public sealed class FixCloseDialogConfirmer : IFixCloseConfirmer
{
    public FixCloseChoice Ask(IReadOnlyList<string> files)
    {
        string list = files.Count == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine
              + string.Join(Environment.NewLine,
                  files.Take(FixDiscardDialogConfirmer.MaxListed).Select(f => $"  · {f}"))
              + (files.Count > FixDiscardDialogConfirmer.MaxListed
                  ? $"{Environment.NewLine}  …y {files.Count - FixDiscardDialogConfirmer.MaxListed} más"
                  : string.Empty);

        MessageBoxResult answer = MessageBox.Show(
            $"El agente dejó {files.Count} fichero(s) modificado(s) en tu clon:" + list
            + Environment.NewLine + Environment.NewLine
            + "Cerrar solo quita esta pantalla de en medio; no revierte nada." + Environment.NewLine
            + Environment.NewLine
            + "  · «Sí» — conservar los cambios y cerrar. Se quedan en tu clon y Atalaya deja de "
            + "ofrecerse a revertirlos." + Environment.NewLine
            + "  · «No» — descartarlos primero (lo mismo que «Descartar todo») y cerrar."
            + Environment.NewLine
            + "  · «Cancelar» — dejarlo todo como está." + Environment.NewLine + Environment.NewLine
            + "¿Conservar los cambios?",
            "Cerrar el arreglo",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        return answer switch
        {
            MessageBoxResult.Yes => FixCloseChoice.ConservarYCerrar,
            MessageBoxResult.No => FixCloseChoice.DescartarYCerrar,
            _ => FixCloseChoice.Cancelar,
        };
    }
}
