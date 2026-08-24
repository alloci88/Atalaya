using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F3.1 Bloque 1 — matcher de 2ª pasada calibrado contra el piloto real 2026-08-24 sobre
/// <c>XBLASTCommon/Class/CommonStatics.cs</c>. Los 4 pares legítimos detectados en el
/// veredicto forense DEBEN matchear; el 5º "nuevo" (Encoding.ASCII) NO debe casar con nada
/// del hub. Ese es el contrato del bloque.
/// </summary>
public class SecondPassMatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 13, 11, 0, TimeSpan.Zero);
    private static readonly TestUlidFactory Ulids = new();
    private const string UnitPath = "XBLASTCommon/Class/CommonStatics.cs";

    private static Finding Existing(string title, string ruleId, int line, FindingStatus status = FindingStatus.Resuelto)
    {
        var stamp = new DetectionStamp(T0.AddDays(-7), AuditMode.Lotes, "old", "alvaro");
        var f = new Finding
        {
            Id = Ulids.NewUlid(),
            Fingerprint = "sha256:" + new string('a', 64),
            RuleId = ruleId,
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Title = title,
            Locations = { new Location(UnitPath, line) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        if (status == FindingStatus.Resuelto)
        {
            f.Resolve(new ResolutionStamp(T0.AddDays(-1), ResolutionVia.Implicita, AuditMode.Lotes, "c", "alvaro", "cubierta"));
        }

        return f;
    }

    private static SubmittedFinding Payload(string title, string ruleId, int line, string? symbol = null)
        => new(ruleId, Pillar.Errores, FindingTag.Checklist, Severity.Alta,
            title, "desc", "impact", "reco",
            new[] { new Location(UnitPath, line) }, symbol);

    [Fact]
    public void StringToByteArray_pair_from_pilot_is_matched()
    {
        var resolved = Existing(
            "StringToByteArray puede lanzar excepción si la cadena excede la longitud destino",
            "errores.null.desreferencia", 92);

        var payload = Payload(
            "StringToByteArray puede lanzar excepción si la cadena excede la longitud especificada",
            "errores.null.desreferencia", 87);

        SecondPassMatch? m = SecondPassMatcher.TryMatch(payload, new[] { resolved });
        m.Should().NotBeNull();
        m!.Score.Should().BeGreaterThan(SecondPassMatcher.Threshold);
    }

    [Fact]
    public void HexStringToByteArray_pair_from_pilot_is_matched()
    {
        var resolved = Existing(
            "HexStringToByteArray descarta silenciosamente el último carácter en cadenas de longitud impar",
            "errores.calculo.negocio", 151);

        var payload = Payload(
            "HexStringToByteArray falla con cadenas hexadecimales de longitud impar",
            "errores.calculo.negocio", 145);

        SecondPassMatch? m = SecondPassMatcher.TryMatch(payload, new[] { resolved });
        m.Should().NotBeNull();
    }

    [Fact]
    public void DateToByteArray_pair_from_pilot_is_matched()
    {
        var resolved = Existing(
            "Asignaciones repetidas de arrays en DateToByteArray/TimeToByteArray",
            "optimizacion.alloc.excesiva", 122);

        var payload = Payload(
            "Asignaciones repetidas de arrays en DateToByteArray/TimeToByteArray",
            "optimizacion.alloc.excesiva", 126);

        SecondPassMatch? m = SecondPassMatcher.TryMatch(payload, new[] { resolved });
        m.Should().NotBeNull();
        m!.Score.Should().Be(1.0); // título literal idéntico
    }

    [Fact]
    public void Encoding_ASCII_new_finding_does_NOT_match_any_pilot_resolved()
    {
        // Todo el catálogo de resueltos del piloto para esa unidad (títulos reales).
        Finding[] resolved =
        {
            Existing("StringToByteArray puede lanzar excepción si la cadena excede la longitud destino", "errores.null.desreferencia", 92),
            Existing("HexStringToByteArray descarta silenciosamente el último carácter en cadenas de longitud impar", "errores.calculo.negocio", 151),
            Existing("ConvertToDetId/ConvertToSeq sin manejo de errores de parseo", "errores.null.desreferencia", 168),
            Existing("Asignaciones repetidas de arrays en DateToByteArray/TimeToByteArray", "optimizacion.alloc.excesiva", 122),
            Existing("Comentarios XML incorrectos/copiados en HexStringToByteArray y ConvertToDetId", "mejoras.estilo.nomenclatura", 160),
            Existing("Formato binario de fecha/hora no documentado ni validado contra protocolo de destino", "criterio.dominio", 108),
            Existing("Falta validación de argumentos nulos en métodos públicos", "errores.null.desreferencia", 85),
        };

        var payload = Payload(
            "Uso de Encoding.ASCII en StringToByteArray puede perder datos silenciosamente",
            "criterio.seguridad", 86);

        SecondPassMatch? m = SecondPassMatcher.TryMatch(payload, resolved);
        m.Should().BeNull("es un problema legítimamente distinto (Encoding.ASCII) aunque comparta el símbolo StringToByteArray");
    }

    [Fact]
    public void No_match_when_path_differs()
    {
        var resolved = Existing("StringToByteArray puede lanzar excepción si la cadena excede la longitud destino",
            "errores.null.desreferencia", 92);
        resolved.Locations.Clear();
        resolved.Locations.Add(new Location("Otro/Path.cs", 92));

        var payload = Payload("StringToByteArray puede lanzar excepción si la cadena excede la longitud especificada",
            "errores.null.desreferencia", 87);

        SecondPassMatcher.TryMatch(payload, new[] { resolved }).Should().BeNull();
    }
}
