using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.3 §3 — la barra de estado no acumula residuos.
/// <para>
/// <b>Origen.</b> Los avisos eran cadenas metidas en una lista de la barra inferior que solo se
/// recortaba —a seis— dentro del tick de sondeo. Es decir: no caducaban nunca. En la captura del
/// usuario había DOS píldoras «Sesión completada: …» a la vez, permanentes, tapando lo único que
/// esa barra debe decir siempre: sync, cuenta y «Auditando…».
/// </para>
/// <para>
/// Las tres invariantes que lo cierran: caducan solos, se descartan de un clic, y de cierre de
/// sesión hay UNO.
/// </para>
/// </summary>
public sealed class ToastCenterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Reloj manejado a mano: la caducidad se prueba sin esperar ocho segundos de verdad.</summary>
    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_toast_disappears_on_its_own_once_its_life_is_over()
    {
        var time = new FixedTime();
        var toasts = new ToastCenter(time);
        toasts.Show("Sesión completada: 3 nuevos", ToastKind.SessionCompleted);

        // Justo antes de caducar sigue ahí: no se descarta antes de que dé tiempo a leerlo.
        time.Now = T0 + ToastCenter.Lifetime - TimeSpan.FromSeconds(1);
        toasts.Sweep();
        toasts.Items.Should().ContainSingle();

        time.Now = T0 + ToastCenter.Lifetime;
        toasts.Sweep();
        toasts.Items.Should().BeEmpty("un aviso es efímero, no un elemento fijo de la barra");
    }

    /// <summary>La regresión exacta de la captura: dos cierres seguidos, una sola píldora.</summary>
    [Fact]
    public void Two_sessions_in_a_row_never_leave_two_pills_stacked()
    {
        var time = new FixedTime();
        var toasts = new ToastCenter(time);

        toasts.Show("Sesión completada: primera", ToastKind.SessionCompleted);
        time.Now = T0 + TimeSpan.FromSeconds(2);   // la segunda termina ANTES de que caduque la primera
        toasts.Show("Sesión completada: segunda", ToastKind.SessionCompleted);

        Toast survivor = toasts.Items.Should().ContainSingle().Subject;
        survivor.Text.Should().Contain("segunda", "la nueva sustituye a la anterior, no se apila encima");
    }

    /// <summary>Y la sustituta hereda vida entera: no se va con el reloj de la que reemplazó.</summary>
    [Fact]
    public void The_replacing_session_toast_gets_a_full_life_of_its_own()
    {
        var time = new FixedTime();
        var toasts = new ToastCenter(time);
        toasts.Show("primera", ToastKind.SessionCompleted);

        time.Now = T0 + TimeSpan.FromSeconds(7);
        toasts.Show("segunda", ToastKind.SessionCompleted);

        time.Now = T0 + ToastCenter.Lifetime;      // la primera ya habría muerto
        toasts.Sweep();
        toasts.Items.Should().ContainSingle().Which.Text.Should().Be("segunda");
    }

    [Fact]
    public void General_notices_do_not_replace_each_other()
    {
        var toasts = new ToastCenter(new FixedTime());

        toasts.Show("No se pudo sincronizar el hub. Revisa «Cuenta».");
        toasts.Show("maria reclamó Pool.cs antes que tú; tu claim se liberó.");

        toasts.Items.Should().HaveCount(2, "son dos hechos distintos, no uno que corrige al otro");
    }

    [Fact]
    public void A_click_dismisses_a_toast_before_it_expires()
    {
        var toasts = new ToastCenter(new FixedTime());
        Toast toast = toasts.Show("Sesión completada", ToastKind.SessionCompleted);

        toasts.Dismiss(toast);

        toasts.Items.Should().BeEmpty();
    }

    [Fact]
    public void The_stack_never_grows_past_what_can_be_read()
    {
        var toasts = new ToastCenter(new FixedTime()) { MaxVisible = 3 };

        for (int i = 1; i <= 6; i++)
        {
            toasts.Show($"aviso {i}");
        }

        toasts.Items.Should().HaveCount(3);
        toasts.Items.Last().Text.Should().Be("aviso 6", "se conservan los más recientes");
    }
}
