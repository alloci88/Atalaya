using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// F4: los silencios dejan de estar nombrados por fingerprint y pasan a estarlo por el ULID del
/// hallazgo que silencian. El ULID de destino sale del propio fichero legado (<c>findingUlids</c>).
/// </summary>
public sealed class SilenceMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly HubPaths _paths;
    private readonly HubStore _store;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public SilenceMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-silmig", Guid.NewGuid().ToString("N"));
        _paths = new HubPaths(_root);
        _store = new HubStore(_paths);
    }

    private void WriteLegacy(string fingerprintHex, string json)
    {
        Directory.CreateDirectory(_paths.SilencesDir("app"));
        File.WriteAllText(Path.Combine(_paths.SilencesDir("app"), $"{fingerprintHex}.json"), json);
    }

    [Fact]
    public void Legacy_silence_is_rekeyed_to_the_finding_ulid_it_referenced()
    {
        Ulid finding = _ulids.NewUlid();
        string hex = new('a', 64);
        WriteLegacy(hex, $$"""
            {
              "schemaVersion": 1,
              "fingerprint": "sha256:{{hex}}",
              "reason": "deuda-aceptada",
              "notes": "aceptado por arquitectura",
              "by": "maria",
              "utc": "2026-02-01T09:00:00+00:00",
              "expiresUtc": null,
              "findingUlids": ["{{finding}}"]
            }
            """);

        SilenceMigration.Result result = SilenceMigration.MigrateApp(_paths, "app");

        result.Migrated.Should().ContainSingle();
        result.Skipped.Should().BeEmpty();

        File.Exists(Path.Combine(_paths.SilencesDir("app"), $"{hex}.json")).Should().BeFalse();

        Silence migrated = _store.TryReadSilence("app", finding)!;
        migrated.Should().NotBeNull();
        migrated.FindingUlid.Should().Be(finding);
        migrated.Reason.Should().Be(SilenceReason.DeudaAceptada);
        migrated.Notes.Should().Be("aceptado por arquitectura");
        migrated.By.Should().Be("maria");
        migrated.ExpiresUtc.Should().BeNull();
    }

    [Fact]
    public void Migration_is_idempotent_and_leaves_already_migrated_files_alone()
    {
        Ulid finding = _ulids.NewUlid();
        _store.WriteSilence("app", new Silence
        {
            FindingUlid = finding,
            By = "maria",
            Utc = DateTimeOffset.UnixEpoch,
            Reason = SilenceReason.FalsoPositivo,
        });

        SilenceMigration.MigrateApp(_paths, "app").Migrated.Should().BeEmpty();
        SilenceMigration.MigrateApp(_paths, "app").Migrated.Should().BeEmpty();

        _store.ListSilences("app").Should().ContainSingle()
            .Which.FindingUlid.Should().Be(finding);
    }

    /// <summary>
    /// Un silencio legado sin <c>findingUlids</c> no tiene a qué hallazgo anclarse. NUNCA se borra
    /// en silencio: se deja donde está y se reporta, para que un humano decida.
    /// </summary>
    [Fact]
    public void Silence_without_a_finding_reference_is_skipped_not_deleted()
    {
        string hex = new('b', 64);
        WriteLegacy(hex, $$"""
            {
              "schemaVersion": 1,
              "fingerprint": "sha256:{{hex}}",
              "reason": "otro",
              "by": "import",
              "utc": "2026-01-01T00:00:00+00:00",
              "findingUlids": []
            }
            """);

        SilenceMigration.Result result = SilenceMigration.MigrateApp(_paths, "app");

        result.Migrated.Should().BeEmpty();
        result.Skipped.Should().ContainSingle().Which.Should().Contain("sin findingUlids");
        File.Exists(Path.Combine(_paths.SilencesDir("app"), $"{hex}.json")).Should().BeTrue();
    }

    [Fact]
    public void Missing_silences_directory_is_a_no_op()
        => SilenceMigration.MigrateApp(_paths, "sin-silencios").Migrated.Should().BeEmpty();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
