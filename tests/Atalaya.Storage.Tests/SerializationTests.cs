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
