using System.Globalization;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F8.1 — un informe sale IGUAL desde cualquier máquina.
/// <para>
/// Estos tests son los únicos que apagan a propósito el <see cref="CultureFixture"/>: se ponen en
/// una cultura hostil —invariante, la del runner de GitHub; y en-US, un Windows en inglés— y
/// comprueban que el informe sigue escribiendo en es-ES. Es lo que separa «funciona porque el
/// proceso está en español» de «funciona porque el informe fija su cultura».
/// </para>
/// <para>
/// Un informe se escribe en el hub y lo lee todo el equipo. Con la cultura ambiente, la misma
/// sesión escrita desde dos máquinas producía dos textos distintos — y «1,234» significa 1,234 en
/// uno y 1234 en el otro. Eso no es un detalle de presentación: es un dato ambiguo de leer.
/// </para>
/// </summary>
public sealed class ReportCultureTests
{
    private static readonly UlidFactory Ulids = new(SystemClock.Instance);

    /// <summary>Corre <paramref name="body"/> en otra cultura y devuelve la que hubiera.</summary>
    private static T InCulture<T>(CultureInfo culture, Func<T> body)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static AppConfig App() => new()
    {
        Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
    };

    private static AuditSession Session(decimal cost) => new()
    {
        Id = Ulids.NewUlid(),
        AppSlug = "app",
        Mode = AuditMode.Lotes,
        By = "alguien",
        Machine = "maquina",
        StartedUtc = new DateTimeOffset(2026, 8, 28, 9, 5, 0, TimeSpan.Zero),
        Usage = new UsageTotals { InputTokens = 1000, OutputTokens = 20, Cost = cost, Currency = "USD" },
    };

    public static TheoryData<string> HostileCultures => new() { string.Empty, "en-US", "de-DE" };

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void El_coste_de_un_informe_se_escribe_en_es_ES_venga_de_donde_venga(string cultureName)
    {
        var hostile = CultureInfo.GetCultureInfo(cultureName);

        string report = InCulture(hostile, () => ReportBuilder.BuildSessionReport(
            App(), Session(67.5m), Array.Empty<Finding>(), pendingUnits: 0, largeUnits: 0, "Org"));

        report.Should().Contain("coste 67,5 USD");
        report.Should().NotContain("coste 67.5");
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void La_fecha_de_un_informe_no_depende_de_la_maquina(string cultureName)
    {
        var hostile = CultureInfo.GetCultureInfo(cultureName);

        string report = InCulture(hostile, () => ReportBuilder.BuildSessionReport(
            App(), Session(1m), Array.Empty<Finding>(), pendingUnits: 0, largeUnits: 0, "Org"));

        // El «:» de HH:mm es el separador de hora de la CULTURA, no un literal: en una cultura con
        // separador «.» esta línea saldría «09.05» sin que nadie lo hubiera pedido.
        report.Should().Contain("**Fecha**: 2026-08-28 09:05 UTC");
    }

    /// <summary>
    /// El mismo informe, generado desde tres culturas distintas, tiene que ser el MISMO texto.
    /// Es la propiedad que de verdad importa de un artefacto compartido, dicha entera.
    /// </summary>
    [Fact]
    public void El_mismo_informe_desde_tres_maquinas_distintas_es_byte_a_byte_el_mismo()
    {
        AuditSession session = Session(1234.5m);

        string invariant = InCulture(CultureInfo.InvariantCulture, () => ReportBuilder.BuildSessionReport(
            App(), session, Array.Empty<Finding>(), 0, 0, "Org"));
        string english = InCulture(CultureInfo.GetCultureInfo("en-US"), () => ReportBuilder.BuildSessionReport(
            App(), session, Array.Empty<Finding>(), 0, 0, "Org"));
        string spanish = InCulture(AppCulture.Display, () => ReportBuilder.BuildSessionReport(
            App(), session, Array.Empty<Finding>(), 0, 0, "Org"));

        invariant.Should().Be(spanish);
        english.Should().Be(spanish);
    }

    /// <summary>
    /// Y el informe de un arreglo asistido, que es el otro que se publica en el hub.
    /// </summary>
    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void El_informe_de_arreglo_tambien_se_escribe_en_es_ES(string cultureName)
    {
        var hostile = CultureInfo.GetCultureInfo(cultureName);
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "commit", "yo");
        var finding = new Finding
        {
            Id = Ulids.NewUlid(),
            RuleId = "criterio.arquitectura",
            Pillar = Pillar.Mejoras,
            Severity = Severity.Media,
            Title = "Título",
            Locations = { new Location("src/A.cs", 3) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };

        AuditSession session = Session(67.5m);
        session.Mode = AuditMode.Fix;

        string report = InCulture(hostile, () => ReportBuilder.BuildFixReport(
            App(), session, finding,
            Array.Empty<(string, string, bool)>(),
            "resumen", null, "título", "descripción", null, "Org"));

        report.Should().Contain("coste 67,5");
        report.Should().Contain("**Fecha**: 2026-08-28 09:05 UTC");
    }

    /// <summary>
    /// La frontera que NO se puede cruzar: los datos para máquinas siguen invariantes. Un
    /// <c>app.json</c> con «67,5» dentro no lo puede volver a leer nadie, y aplicar la cultura de
    /// presentación al JSON habría sido un fallo mucho peor que el que abrió esta tanda.
    /// </summary>
    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void El_json_del_hub_sigue_siendo_invariante(string cultureName)
    {
        var hostile = CultureInfo.GetCultureInfo(cultureName);

        string json = InCulture(hostile, () => Atalaya.Storage.Json.AtalayaJson.Serialize(
            new UsageTotals { InputTokens = 1000, Cost = 67.5m }));

        json.Should().Contain("67.5", "el JSON es para máquinas: punto decimal, siempre");
        json.Should().NotContain("67,5");
    }

    /// <summary>El alias legible es identidad persistida, no presentación: dígitos y nada más.</summary>
    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void El_alias_legible_no_cambia_con_la_cultura(string cultureName)
    {
        var hostile = CultureInfo.GetCultureInfo(cultureName);

        InCulture(hostile, () => DisplayId.Format(Pillar.Errores, 42)).Should().Be("BUG-0042");
    }
}
