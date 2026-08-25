using System.Collections.ObjectModel;

namespace Atalaya.App.Services;

/// <summary>Qué clase de aviso es, que es lo que decide si sustituye a otro (F5.3 §3).</summary>
public enum ToastKind
{
    /// <summary>Aviso suelto: fallo de sync, credenciales, recuperación. Se apilan y expiran.</summary>
    General,

    /// <summary>
    /// Cierre de sesión. Solo puede haber UNO: la sesión siguiente sustituye a la anterior en vez
    /// de apilarse encima.
    /// </summary>
    SessionCompleted,
}

/// <summary>Un aviso efímero con su fecha de caducidad. Nunca un elemento fijo de la interfaz.</summary>
public sealed class Toast
{
    public required string Text { get; init; }

    public required ToastKind Kind { get; init; }

    /// <summary>Cuándo se auto-descarta. La barra de estado no acumula residuos.</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>
/// Los avisos efímeros de la carcasa (F5.3 §3).
/// <para>
/// <b>Por qué existe.</b> Los avisos eran cadenas metidas en una lista de la barra de estado que
/// solo se recortaba —a seis— en el tick de sondeo. Es decir: no caducaban. Tras dos auditorías
/// seguidas quedaban dos píldoras «Sesión completada: …» una al lado de la otra, permanentes,
/// tapando lo único que la barra debe decir siempre: sync, cuenta y «Auditando…».
/// </para>
/// <para>
/// <b>Las tres reglas.</b> (1) Todo aviso caduca solo, a los <see cref="Lifetime"/>. (2) De cierre
/// de sesión hay UNO: el nuevo sustituye al viejo. (3) El resumen permanente de la última sesión
/// no vive aquí, sino en el item «Última sesión» del rail, que es donde se puede volver a leer.
/// </para>
/// <para>
/// La caducidad se barre desde fuera (<see cref="Sweep"/>) en vez de con un temporizador propio:
/// así el reloj lo pone quien llama y los tests son deterministas, sin esperas reales.
/// </para>
/// </summary>
public sealed class ToastCenter
{
    /// <summary>Lo que dura un aviso en pantalla. Suficiente para leerlo, poco para estorbar.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    private readonly TimeProvider _time;

    public ToastCenter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Los avisos visibles ahora mismo, del más antiguo al más nuevo.</summary>
    public ObservableCollection<Toast> Items { get; } = new();

    /// <summary>Tope de avisos a la vez: más que esto ya no se lee, se sufre.</summary>
    public int MaxVisible { get; init; } = 3;

    /// <summary>
    /// Muestra un aviso. Un <see cref="ToastKind.SessionCompleted"/> retira el anterior de su
    /// misma clase antes de entrar, que es la regla de «solo uno a la vez».
    /// </summary>
    public Toast Show(string text, ToastKind kind = ToastKind.General)
    {
        DateTimeOffset now = _time.GetUtcNow();
        SweepAt(now);

        if (kind == ToastKind.SessionCompleted)
        {
            foreach (Toast previous in Items.Where(t => t.Kind == ToastKind.SessionCompleted).ToList())
            {
                Items.Remove(previous);
            }
        }

        var toast = new Toast { Text = text, Kind = kind, ExpiresUtc = now + Lifetime };
        Items.Add(toast);

        while (Items.Count > MaxVisible)
        {
            Items.RemoveAt(0);
        }

        return toast;
    }

    /// <summary>Descarte manual: un clic en el aviso lo quita sin esperar a que caduque.</summary>
    public void Dismiss(Toast? toast)
    {
        if (toast is not null)
        {
            Items.Remove(toast);
        }
    }

    /// <summary>Retira lo caducado. Lo llama el temporizador de la ventana, cada segundo.</summary>
    public void Sweep() => SweepAt(_time.GetUtcNow());

    /// <summary>El barrido con un reloj explícito: el gancho que hace deterministas los tests.</summary>
    public void SweepAt(DateTimeOffset now)
    {
        foreach (Toast expired in Items.Where(t => t.ExpiresUtc <= now).ToList())
        {
            Items.Remove(expired);
        }
    }
}
