using Atalaya.Domain.Model;
using Atalaya.Storage.Sync;
using Atalaya.Tests;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// <b>BUGFIX-PUSH — dos personas publicando contra el mismo hub a la vez.</b>
/// <para>
/// <b>Por qué existe.</b> El hub prometía desde el primer día «pull → rebase → push con resolución
/// de conflictos», y hasta aquí <b>nadie lo había ejercitado en concurrencia</b>: los tests de dos
/// clones publican por turnos, uno después de otro, que es el caso fácil. El día que dos sesiones
/// reales coincidieron —dos personas auditando aplicaciones distintas contra el mismo hub— una se
/// quedó colgada. Ésta es la prueba de carga que faltaba.
/// </para>
/// <para>
/// <b>Lo que fija.</b> Que las dos <b>terminen</b> y que <b>ninguna pierda lo suyo</b>: las
/// reclamaciones de las dos acaban en el hub. Es lo que se rompe en silencio — un push que se
/// rinde deja el trabajo local sin publicar y nadie se entera hasta que otra máquina audita la
/// misma unidad, que es justo lo que las reclamaciones vienen a evitar.
/// </para>
/// <para>
/// Contra el <c>--bare</c> local y sin red, como manda N-1.
/// </para>
/// <para>
/// <b>Lo que encontró, y cómo se cerró (F31).</b> Con los dos clones publicando a la vez, las dos
/// llamadas devolvían <c>true</c> y en el hub quedaba <b>una sola</b> reclamación: la del que
/// perdía la carrera se quedaba en su clon creyendo que se publicó. La causa que faltaba no estaba
/// en el rebase, sino en qué se aceptaba como prueba de haber publicado: <c>Network.Push</c> vuelve
/// sin excepción y sin rechazo aunque el hub NO se haya quedado con el commit. Ahora una
/// publicación no se da por buena hasta releer el remoto y ver el commit propio en su punta.
/// </para>
/// </summary>
public sealed class ConcurrentClaimsTests : IDisposable
{
    private readonly TempRepo _remote = new();

    public void Dispose() => _remote.Dispose();

    private (HubSyncService Sync, HubStore Store) Clone(string name, string who)
    {
        var paths = new HubPaths(_remote.NewClonePath(name));
        var sync = new HubSyncService(paths, (who, $"{who}@example.com"));
        sync.EnsureCloned(_remote.BareRemotePath);
        return (sync, new HubStore(paths));
    }

    [Fact]
    public void Dos_sesiones_reclamando_a_la_vez_terminan_las_dos_y_no_se_pisan()
    {
        // Una línea base que las dos comparten, para que las dos tengan de dónde divergir.
        (HubSyncService seedSync, HubStore seedStore) = Clone("seed", "semilla");
        seedStore.WriteHub(Samples.Hub());
        seedStore.WriteApp(Samples.App());
        seedSync.CommitAndPush("seed").Should().BeTrue(seedSync.Why());

        (HubSyncService aSync, HubStore aStore) = Clone("a", "alvaro");
        (HubSyncService bSync, HubStore bStore) = Clone("b", "daniel");

        aStore.WriteClaim("webapp", Samples.Claim("src/A.cs", "alvaro"));
        bStore.WriteClaim("webapp", Samples.Claim("src/B.cs", "daniel"));

        // A LA VEZ, que es lo que ningún test hacía. Una de las dos se encontrará el remoto movido
        // debajo y tendrá que rehacer el rebase; ésa es la rama que nunca se recorría.
        var ready = new Barrier(2);
        bool a = false;
        bool b = false;

        var first = new Thread(() =>
        {
            ready.SignalAndWait();
            a = aSync.CommitAndPush("claims: alvaro 1 unidades en webapp");
        });
        var second = new Thread(() =>
        {
            ready.SignalAndWait();
            b = bSync.CommitAndPush("claims: daniel 1 unidades en webapp");
        });

        first.Start();
        second.Start();
        first.Join(TimeSpan.FromMinutes(1)).Should().BeTrue("una publicación no puede no volver");
        second.Join(TimeSpan.FromMinutes(1)).Should().BeTrue("una publicación no puede no volver");

        a.Should().BeTrue("la primera publicó");
        b.Should().BeTrue("y la segunda rehízo el rebase y publicó también");

        // Y LO DE LAS DOS ESTÁ EN EL HUB. Que las dos digan «sí» no basta: lo que importa es que
        // ninguna se llevara por delante la reclamación de la otra al rebasar.
        (HubSyncService checkSync, HubStore checkStore) = Clone("check", "quien-mira");
        checkSync.Pull();

        var claims = checkStore.ListClaims("webapp").Select(c => c.Unit).ToList();
        claims.Should().Contain("src/A.cs");
        claims.Should().Contain("src/B.cs");
    }
}
