using System.Runtime.InteropServices;

namespace Atalaya.App.Services;

/// <summary>
/// Escribir por la consola de quien nos lanzó (BUGFIX-ARRANQUE).
/// <para>
/// Atalaya es una aplicación de ventana (<c>WinExe</c>), y una aplicación de ventana <b>no tiene
/// consola</b>: lo que escriba en <c>Console.Out</c> no llega a ninguna parte. Para
/// <c>--selfcheck</c> eso no vale — quien lo ejecuta es un workflow que necesita leer el parte—,
/// así que hay que engancharse a la consola del proceso padre. Es la vía estándar de Windows para
/// que un mismo ejecutable sirva de ventana y de orden de consola.
/// </para>
/// <para>
/// Si no hay consola padre —doble clic en el Explorador— no pasa nada: se escribe en el log y en
/// el fichero de parte, que son los otros dos sitios donde se deja.
/// </para>
/// </summary>
internal static class NativeConsole
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private static bool _attached;

    public static void Write(string text)
    {
        try
        {
            if (!_attached)
            {
                _attached = AttachConsole(AttachParentProcess);
            }

            Console.Out.Write(text);
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // Un parte que no se puede imprimir no puede tumbar el chequeo que lo produjo.
        }
    }
}
