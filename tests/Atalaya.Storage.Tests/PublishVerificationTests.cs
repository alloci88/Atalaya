using Atalaya.Storage.Sync;
using Atalaya.Tests;
using FluentAssertions;
using LibGit2Sharp;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// <b>F31 — un «publicado» tiene que significar publicado.</b>
/// <para>
/// La regla, en una frase: <b>ninguna publicación devuelve éxito sin haber comprobado que su
/// commit es antepasado de la punta del hub después del push</b>. Existe porque lo contrario se
/// midió: <c>Network.Push</c> vuelve sin excepción y sin rechazo aunque el hub NO se haya quedado
/// con el commit, y entonces la sesión se queda en su clon creyendo que publicó. Cuesta una
/// lectura más del remoto por publicación, y eso es exactamente lo que se está comprando.
/// </para>
/// <para>Contra el <c>--bare</c> local y sin red, como manda N-1.</para>
/// </summary>
public sealed class PublishVerificationTests : IDisposable
{
    private readonly TempRepo _remote = new();

    public void Dispose() => _remote.Dispose();

    private HubSyncService Clone(string name)
    {
        var sync = new HubSyncService(new HubPaths(_remote.NewClonePath(name)), (name, name + "@example.com"));
        sync.EnsureCloned(_remote.BareRemotePath);
        return sync;
    }

    private (HubSyncService Sync, string Path) Seeded(string name)
    {
        HubSyncService sync = Clone(name);
        var store = new HubStore(new HubPaths(_remote.NewClonePath(name)));
        store.WriteHub(Samples.Hub());
        store.WriteApp(Samples.App());
        sync.CommitAndPush("seed").Should().BeTrue(sync.Why());

        // Un segundo commit, para tener una punta ANTERIOR a la que retroceder.
        store.WriteClaim("webapp", Samples.Claim("src/Uno.cs", name));
        sync.CommitAndPush("claims: " + name).Should().BeTrue(sync.Why());
        return (sync, _remote.NewClonePath(name));
    }

    [Fact]
    public void Un_commit_que_esta_en_la_punta_del_hub_pasa_la_comprobacion()
    {
        (HubSyncService sync, string path) = Seeded("a");

        using var repo = new Repository(path);
        Commit mine = repo.Head.Tip;

        // Recién publicado: el hub lo tiene, así que la comprobación calla.
        sync.Invoking(s => s.VerifyPublished(repo.Head.FriendlyName, mine, TimeSpan.Zero))
            .Should().NotThrow();

        sync.Dispose();
    }

    [Fact]
    public void Un_commit_que_el_hub_NO_tiene_no_cuenta_como_publicado()
    {
        (HubSyncService sync, string path) = Seeded("a");

        using var repo = new Repository(path);
        string branch = repo.Head.FriendlyName;
        Commit mine = repo.Head.Tip;
        Commit before = mine.Parents.First();

        // EL HUB SE DESHACE DE NUESTRO COMMIT A NUESTRAS ESPALDAS. Es lo que deja un push que se
        // pisa con otro: los objetos siguen ahí, la punta ya no los alcanza, y nadie nos avisó.
        using (var bare = new Repository(_remote.BareRemotePath))
        {
            bare.Refs.UpdateTarget(bare.Refs["refs/heads/" + branch], before.Id);
        }

        sync.Invoking(s => s.VerifyPublished(branch, mine, TimeSpan.Zero))
            .Should().Throw<NonFastForwardException>()
            .WithMessage("*" + mine.Sha[..8] + "*", "el motivo tiene que decir QUÉ commit se perdió")
            .WithMessage("*" + before.Sha[..8] + "*", "y contra qué punta se comprobó");

        sync.Dispose();
    }

    [Fact]
    public void Y_lo_que_el_hub_no_se_quedo_sigue_pendiente_de_publicar()
    {
        (HubSyncService sync, string path) = Seeded("a");

        string branch;
        Commit mine;
        using (var repo = new Repository(path))
        {
            branch = repo.Head.FriendlyName;
            mine = repo.Head.Tip;
            using var bare = new Repository(_remote.BareRemotePath);
            bare.Refs.UpdateTarget(bare.Refs["refs/heads/" + branch], mine.Parents.First().Id);
        }

        // Lo que NO se perdió: el commit sigue en el clon y vuelve a contarse como pendiente en
        // cuanto se relee el hub. Ésa es la red que hace que un «publicado» falso no cueste trabajo.
        sync.Pull();
        sync.PendingCommits.Should().Be(1, "el hub ya no lo tiene, así que está sin publicar");

        // Y la siguiente publicación lo saca.
        sync.CommitAndPush("reintento").Should().BeTrue(sync.Why());
        sync.Pull();
        sync.PendingCommits.Should().Be(0);

        sync.Dispose();
    }

    /// <summary>
    /// <b>Una publicación que no sale DICE POR QUÉ</b> (BUGFIX-CI-2).
    /// <para>
    /// <b>El defecto que cierra.</b> Agotar los cinco intentos por rechazo devolvía <c>false</c> con
    /// la salud en rojo y <c>LastError</c> a <c>null</c> —lo pone a null el <see cref="HubSyncService.Pull"/>
    /// de cada vuelta al salir bien—, así que el parte de un runner decía «Expected … to be True» y
    /// nada más. En esta máquina se depura con el registro delante; del runner solo vuelve el
    /// <c>.trx</c>, y un <c>false</c> pelado no se diagnostica.
    /// </para>
    /// <para>
    /// <b>Cómo se provoca, y por qué así.</b> Contra el <c>--bare</c> de N-1 no se pueden pedir cinco
    /// rechazos de verdad: el transporte local <b>no arbitra nada</b> (D-1023), así que todo push es
    /// un avance rápido y nadie rechaza. Lo que sí se puede es dejar al clon <b>sin traerse su propia
    /// rama</b>: el push escribe en el bare y la relectura de D-1023 no la ve nunca, que es
    /// exactamente el rechazo que el bucle fabrica cuando el hub no se quedó con lo nuestro. Cinco
    /// vueltas, y al final el motivo tiene que estar escrito.
    /// </para>
    /// <para>
    /// Se mira <see cref="HubSyncService.LastPublishFailure"/> y <b>no</b> <c>LastError</c>: esa
    /// otra es la frase que se le enseña al usuario —el globo del piloto, la página de Cuenta— y
    /// no cambia, que es la mitad de esta corrección que no se ve.
    /// </para>
    /// </summary>
    [Fact]
    public void Una_publicacion_que_agota_los_intentos_deja_dicho_por_que()
    {
        HubSyncService sync = Clone("a");
        string path = _remote.NewClonePath("a");
        new HubStore(new HubPaths(path)).WriteHub(Samples.Hub());

        using (var repo = new Repository(path))
        {
            repo.Network.Remotes.Update(
                "origin", r => r.FetchRefSpecs = new[] { "+refs/heads/nadie/*:refs/remotes/origin/nadie/*" });
        }

        sync.CommitAndPush("seed").Should().BeFalse("la relectura no puede ver la rama publicada");

        sync.Health.Should().Be(SyncHealth.Red);
        sync.LastPublishFailure.Should().NotBeNullOrWhiteSpace(
                "un false sin motivo es lo único que llega de Actions, y no se diagnostica")
            .And.Contain($"{HubSyncService.PushAttempts} intentos", "cuántas veces se intentó")
            .And.Contain("sigue sin la rama", "y qué dijo cada intento")
            .And.MatchRegex(@"tras \d+ ms", "y cuánto tardó, que es lo que separa disputa de lentitud");

        sync.Dispose();
    }
}
