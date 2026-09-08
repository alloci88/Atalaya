using Atalaya.Storage.Sync;
using Atalaya.Tests;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// <b>F31 §2 — un candado de una sesión muerta no se hereda en silencio.</b>
/// <para>
/// Cerrar Atalaya a la fuerza en mitad de una escritura deja un <c>index.lock</c> en el clon. Git
/// no lo quita solo, así que la sesión siguiente se encuentra un clon que rechaza toda escritura
/// y un mensaje de libgit2 que no le dice a nadie qué hacer. Se detecta al abrir el clon, se
/// limpia, y se cuenta.
/// </para>
/// <para>Contra el <c>--bare</c> local y sin red, como manda N-1.</para>
/// </summary>
public sealed class StaleLockTests : IDisposable
{
    private readonly TempRepo _remote = new();

    public void Dispose() => _remote.Dispose();

    private HubSyncService Open(string name)
    {
        var sync = new HubSyncService(new HubPaths(_remote.NewClonePath(name)), (name, name + "@x.com"));
        sync.EnsureCloned(_remote.BareRemotePath);
        return sync;
    }

    private string GitDir(string name) => Path.Combine(_remote.NewClonePath(name), ".git");

    [Fact]
    public void Un_index_lock_huerfano_se_detecta_se_dice_y_se_limpia()
    {
        Open("a").Dispose();

        // Lo que deja un proceso muerto: el fichero, sin nadie dentro.
        string lockFile = Path.Combine(GitDir("a"), "index.lock");
        File.WriteAllText(lockFile, string.Empty);

        using HubSyncService reopened = Open("a");

        reopened.ClearedStaleLocks.Should().Contain("index.lock", "hay que DECIRLO, no solo quitarlo");
        reopened.LocksInUse.Should().BeEmpty();
        File.Exists(lockFile).Should().BeFalse("y el clon queda utilizable");
    }

    [Fact]
    public void Y_el_clon_vuelve_a_escribir_despues_de_limpiarlo()
    {
        Open("a").Dispose();
        File.WriteAllText(Path.Combine(GitDir("a"), "index.lock"), string.Empty);

        using HubSyncService reopened = Open("a");
        var store = new HubStore(new HubPaths(_remote.NewClonePath("a")));
        store.WriteHub(Samples.Hub());
        store.WriteApp(Samples.App());

        reopened.CommitAndPush("después del candado").Should().BeTrue(reopened.Why());
    }

    [Fact]
    public void Un_candado_de_referencia_tambien_se_quita()
    {
        Open("a").Dispose();

        string refLock = Path.Combine(GitDir("a"), "refs", "heads", "master.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(refLock)!);
        File.WriteAllText(refLock, string.Empty);

        using HubSyncService reopened = Open("a");

        reopened.ClearedStaleLocks.Should().Contain("refs/heads/master.lock");
        File.Exists(refLock).Should().BeFalse();
    }

    [Fact]
    public void Un_candado_que_alguien_tiene_ABIERTO_no_es_huerfano_y_no_se_toca()
    {
        Open("a").Dispose();

        string lockFile = Path.Combine(GitDir("a"), "index.lock");

        // Un candado VIVO: el proceso que lo puso lo tiene abierto. Borrarlo sería llevarse por
        // delante una operación en curso, que es peor que el problema que se venía a resolver.
        using var held = new FileStream(lockFile, FileMode.Create, FileAccess.Write, FileShare.None);

        using HubSyncService reopened = Open("a");

        reopened.ClearedStaleLocks.Should().BeEmpty();
        reopened.LocksInUse.Should().Contain("index.lock", "se dice que hay alguien dentro");
        File.Exists(lockFile).Should().BeTrue("no era nuestro para quitarlo");
    }
}
