using Atalaya.Domain;
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
}
