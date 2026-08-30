using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage.Json;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

public class SerializationTests
{
    [Fact]
    public void Finding_roundtrips()
    {
        Finding original = Samples.Finding();

        string json = AtalayaJson.Serialize(original);
        Finding back = AtalayaJson.Deserialize<Finding>(json);

        back.Id.Should().Be(original.Id);
        back.Severity.Should().Be(Severity.Critica);
        back.Confidence.Should().Be(Confidence.Media);
        back.Status.Should().Be(FindingStatus.Activo);
        back.Locations.Should().ContainSingle().Which.Line.Should().Be(120);
        back.History.Should().ContainSingle();
    }

    /// <summary>
    /// H9.1 §1 — el evento de un arreglo apunta a SU sesión, y eso viaja al hub. Es un campo nuevo
    /// en un fichero que ya está escrito por ahí: lo que no puede pasar es que una entrada
    /// anterior, sin el campo, deje de leerse.
    /// </summary>
    [Fact]
    public void History_entry_roundtrips_its_session_id()
    {
        Finding original = Samples.Finding();
        original.History.Add(new HistoryEntry(
            Samples.T0.AddMinutes(5), FindingEvent.FixProposed, "alvaro", "arreglo asistido ejecutado")
        {
            SessionId = "01J8ZC3K9Q0000000000000000",
        });

        string json = AtalayaJson.Serialize(original);
        Finding back = AtalayaJson.Deserialize<Finding>(json);

        json.Should().Contain("\"sessionId\": \"01J8ZC3K9Q0000000000000000\"");
        back.History.Should().HaveCount(2);
        back.History.Last().SessionId.Should().Be("01J8ZC3K9Q0000000000000000");
        back.History.First().SessionId.Should().BeNull("la entrada de siempre no lo trae y se lee igual");
    }

    /// <summary>Un historial escrito ANTES de H9.1 —sin la clave— se sigue leyendo sin ruido.</summary>
    [Fact]
    public void A_history_entry_written_before_the_session_link_still_loads()
    {
        string json = AtalayaJson.Serialize(Samples.Finding()).Replace("\"sessionId\"", "\"ignorado\"");

        Finding back = AtalayaJson.Deserialize<Finding>(json);

        back.History.Should().ContainSingle().Which.SessionId.Should().BeNull();
    }

    [Fact]
    public void Enum_wire_values_match_the_schema()
    {
        string json = AtalayaJson.Serialize(Samples.Finding());

        json.Should().Contain("\"pillar\": \"errores\"");
        json.Should().Contain("\"severity\": \"critica\"");
        json.Should().Contain("\"confidence\": \"media\"");
        json.Should().Contain("\"status\": \"activo\"");
        json.Should().Contain("\"tag\": \"criterio\"");
        json.Should().Contain("\"origin\": \"lotes\"");
        json.Should().Contain("\"event\": \"detected\"");
    }

    [Fact]
    public void Property_names_are_camelCase()
    {
        string json = AtalayaJson.Serialize(Samples.Finding());

        json.Should().Contain("\"schemaVersion\": 1");
        json.Should().Contain("\"displayId\"");
        json.Should().Contain("\"timesConfirmed\"");
        json.Should().Contain("\"firstDetected\"");
    }

    [Fact]
    public void Timestamps_are_utc_with_Z_suffix()
    {
        string json = AtalayaJson.Serialize(Samples.Finding());

        json.Should().Contain("2026-08-21T10:00:00Z");
    }

    [Fact]
    public void Silence_reason_uses_kebab_case()
    {
        var silence = new Silence
        {
            FindingUlid = Samples.Finding().Id,
            By = "maria",
            Utc = Samples.T0,
            Reason = SilenceReason.DecisionArquitectonica,
        };

        string json = AtalayaJson.Serialize(silence);
        json.Should().Contain("\"reason\": \"decision-arquitectonica\"");

        AtalayaJson.Deserialize<Silence>(json).Reason.Should().Be(SilenceReason.DecisionArquitectonica);
    }

    [Fact]
    public void Deserialize_rejects_unknown_enum_value()
    {
        string bad = AtalayaJson.Serialize(Samples.Finding()).Replace("\"critica\"", "\"catastrofica\"");

        var act = () => AtalayaJson.Deserialize<Finding>(bad);
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void Output_uses_lf_and_trailing_newline()
    {
        string json = AtalayaJson.Serialize(Samples.Hub());

        json.Should().NotContain("\r\n");
        json.Should().EndWith("\n");
    }
    /// <summary>
    /// F9 §2. El registro de arreglo es un HECHO del hub: viaja como todo lo demás y valida como
    /// todo lo demás. Sin ficheros con huella no reconocería ningún commit, así que no es válido.
    /// </summary>
    [Fact]
    public void El_registro_de_arreglo_va_y_vuelve_intacto()
    {
        var record = new FixRecord
        {
            Id = Ulid.Parse("01J0000000000000000000000A"),
            AppSlug = "app",
            FindingId = "01J0000000000000000000000B",
            FindingAlias = "BUG-0003",
            Utc = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero),
            By = "quien",
            BaseCommit = "abc1234",
            Files = { new FixFileStamp("src/A.cs", "sha256:deadbeef") },
        };

        string json = AtalayaJson.Serialize(record);
        FixRecord back = AtalayaJson.Deserialize<FixRecord>(json);

        back.Id.Should().Be(record.Id);
        back.AppSlug.Should().Be("app");
        back.FindingAlias.Should().Be("BUG-0003");
        back.BaseCommit.Should().Be("abc1234");
        back.Files.Should().ContainSingle().Which.Should().Be(record.Files[0]);
    }

    [Fact]
    public void Un_registro_de_arreglo_sin_ficheros_no_pasa_el_esquema()
    {
        var record = new FixRecord
        {
            Id = Ulid.Parse("01J0000000000000000000000A"),
            AppSlug = "app",
            By = "quien",
        };

        Action act = () => SchemaValidation.Validate(record);

        act.Should().Throw<SchemaValidationException>().WithMessage("*files*");
    }
}
