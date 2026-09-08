using Atalaya.Domain.Model;
using Atalaya.Storage.Sync;
using Atalaya.Tests;
using FluentAssertions;
using LibGit2Sharp;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// <b>F31 §3 — el mismo fichero del hub tocado por dos personas, por tipo.</b>
/// <para>
/// Las reglas por tipo ya existían y ya tenían prueba para reclamaciones e inventario
/// (<see cref="TwoCloneSyncTests"/>). Faltaban las dos que nadie había ejercitado con los dos
/// clones editando <b>lo mismo</b>: el hallazgo y la sesión. Y falta, sobre todas, la que las
/// gobierna a todas: <b>ninguna resolución automática borra el trabajo de nadie</b> — lo que
/// pierde queda en el historial de git, recuperable.
/// </para>
/// <para>Contra el <c>--bare</c> local y sin red, como manda N-1.</para>
/// </summary>
public sealed class ConflictRulesTests : IDisposable
{
    private readonly TempRepo _remote = new();

    public void Dispose() => _remote.Dispose();

    private (HubSyncService Sync, HubStore Store) Clone(string name, string who)
    {
        var paths = new HubPaths(_remote.NewClonePath(name));
        var sync = new HubSyncService(paths, (who, who + "@example.com"));
        sync.EnsureCloned(_remote.BareRemotePath);
        return (sync, new HubStore(paths));
    }

    [Fact]
    public void Dos_sesiones_editando_el_MISMO_hallazgo_gana_la_ultima_y_la_otra_queda_en_el_historial()
    {
        (HubSyncService aSync, HubStore aStore) = Clone("a", "alvaro");
        aStore.WriteHub(Samples.Hub());
        aStore.WriteApp(Samples.App());
        Finding seed = Samples.Finding();
        aStore.WriteFinding("webapp", seed);
        aSync.CommitAndPush("seed").Should().BeTrue(aSync.Why());

        (HubSyncService bSync, HubStore bStore) = Clone("b", "maria");

        string ulid = seed.Id.ToString();
        var pronto = new DateTimeOffset(2025, 9, 6, 10, 42, 0, TimeSpan.Zero);
        var tarde = new DateTimeOffset(2025, 9, 6, 11, 15, 0, TimeSpan.Zero);

        // Los dos tocan el MISMO fichero de hallazgo, cada uno con su veredicto y su fecha.
        Finding mio = aStore.TryReadFinding("webapp", ulid)!;
        mio.History.Add(new HistoryEntry(pronto, FindingEvent.Confirmed, "alvaro", "lo confirmo yo"));
        aStore.WriteFinding("webapp", mio);
        aSync.Commit("alvaro confirma");

        Finding suyo = bStore.TryReadFinding("webapp", ulid)!;
        suyo.History.Add(new HistoryEntry(tarde, FindingEvent.Confirmed, "maria", "lo confirmo yo tambien"));
        bStore.WriteFinding("webapp", suyo);
        bSync.Commit("maria confirma");

        aSync.Push().Should().BeTrue(aSync.Why());
        bSync.Push().Should().BeTrue(bSync.Why());

        // GANA EL ULTIMO POR FECHA, Y EL OTRO NO SE PIERDE: sigue en el historial, con su autor.
        Finding merged = bStore.TryReadFinding("webapp", ulid)!;
        merged.History.Should().Contain(h => h.By == "maria" && h.Utc == tarde);
        merged.History.Should().Contain(h => h.By == "alvaro" && h.Utc == pronto,
            "el que pierde el veredicto no pierde su entrada");
        merged.History.Should().BeInAscendingOrder(h => h.Utc);

        // Y el hub coincide con lo que ve quien lo mire de nuevo.
        (HubSyncService cSync, HubStore cStore) = Clone("c", "quien-mira");
        cSync.Pull();
        Finding enElHub = cStore.TryReadFinding("webapp", ulid)!;
        enElHub.History.Should().Contain(h => h.By == "alvaro");
        enElHub.History.Should().Contain(h => h.By == "maria");
    }

    [Fact]
    public void Dos_sesiones_a_la_vez_no_pueden_chocar_en_sus_ficheros_de_sesion()
    {
        (HubSyncService aSync, HubStore aStore) = Clone("a", "alvaro");
        aStore.WriteHub(Samples.Hub());
        aStore.WriteApp(Samples.App());
        aSync.CommitAndPush("seed").Should().BeTrue(aSync.Why());

        (HubSyncService bSync, HubStore bStore) = Clone("b", "maria");

        // Un fichero por sesion, con su ULID: no hay nombre que compartir.
        AuditSession suya = Samples.Session(by: "alvaro");
        AuditSession mia = Samples.Session(by: "maria");
        suya.Id.Should().NotBe(mia.Id);

        aStore.WriteSession(suya);
        bStore.WriteSession(mia);

        aSync.Commit("session: alvaro");
        bSync.Commit("session: maria");
        aSync.Push().Should().BeTrue(aSync.Why());

        // Si esto llegara a notificar un conflicto, seria un DEFECTO: dos sesiones no comparten
        // fichero. El test esta aqui para cazarlo.
        var notifications = new List<string>();
        bSync.Pulled += r => notifications.AddRange(r.Notifications);
        bSync.Push().Should().BeTrue(bSync.Why());
        notifications.Should().BeEmpty("dos sesiones no pueden pisarse: cada una tiene su fichero");

        (HubSyncService cSync, HubStore cStore) = Clone("c", "quien-mira");
        cSync.Pull();
        cStore.ListSessions("webapp").Select(s => s.By)
            .Should().BeEquivalentTo(new[] { "alvaro", "maria" }, "las dos estan");
    }

    [Fact]
    public void La_reclamacion_que_pierde_queda_recuperable_en_el_registro_de_git()
    {
        (HubSyncService aSync, HubStore aStore) = Clone("a", "alvaro");
        aStore.WriteHub(Samples.Hub());
        aStore.WriteApp(Samples.App());
        aSync.CommitAndPush("seed").Should().BeTrue(aSync.Why());

        (HubSyncService bSync, HubStore bStore) = Clone("b", "maria");

        // Los dos reclaman la MISMA unidad: mismo hash, mismo fichero.
        aStore.WriteClaim("webapp", Samples.Claim("src/Db/Pool.cs", "alvaro"));
        aSync.Commit("claim: alvaro");
        bStore.WriteClaim("webapp", Samples.Claim("src/Db/Pool.cs", "maria"));
        bSync.Commit("claim: maria");

        aSync.Push().Should().BeTrue(aSync.Why());
        bSync.Push().Should().BeTrue(bSync.Why());

        // La regla: gana la que ya estaba publicada.
        bStore.ListClaims("webapp").Should().ContainSingle().Which.By.Should().Be("alvaro");

        // PERO LA DE MARIA NO SE HA BORRADO DEL MUNDO, Y AQUI ESTA DONDE VIVE, QUE NO ES DONDE SE
        // ESPERABA. El rebase REESCRIBE el commit de Maria, asi que su commit original ya no esta
        // en la rama y `git log` no lo encuentra: recorrer los commits de HEAD no da con el. Sigue
        // entero en el REGISTRO de referencias del clon —el reflog—, que es de donde se recupera
        // el trabajo que un rebase deja atras. Es una diferencia con consecuencias y por eso se
        // fija aqui: el reflog CADUCA (noventa dias por defecto), asi que «recuperable» tiene
        // fecha de caducidad, y quien tenga que recuperar algo no puede enterarse dentro de un ano.
        using var repo = new Repository(_remote.NewClonePath("b"));

        var perdidos = repo.Refs.Log(repo.Refs.Head.CanonicalName)
            .Select(entry => repo.Lookup<Commit>(entry.To))
            .Where(c => c is not null)
            .Where(c => c!.Message.Contains("claim: maria", StringComparison.Ordinal))
            .ToList();

        perdidos.Should().NotBeEmpty("el commit que la escribio sigue en el registro del clon");

        string ruta = "apps/webapp/claims/"
            + HubPaths.HashToFileName(Samples.Claim("src/Db/Pool.cs", "maria").UnitHash) + ".json";
        var blob = perdidos[0]![ruta]?.Target as Blob;
        blob.Should().NotBeNull("y de ese commit se saca el fichero entero");
        blob!.GetContentText().Should().Contain("maria", "con su autor dentro");
    }
}
