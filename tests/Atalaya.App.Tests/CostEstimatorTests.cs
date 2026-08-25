using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.6 §4 — la estimación de coste del lanzamiento. Lo que se prueba aquí es la regla N-2
/// aplicada al dinero: el número sale del gasto YA medido o no existe, y siempre viene con su
/// procedencia. Un total inventado es peor que no dar ninguno.
/// </summary>
public sealed class CostEstimatorTests
{
    private static readonly UlidFactory Ulids = new(SystemClock.Instance);
    private static readonly DateTimeOffset Day = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Una sesión con coste medido por unidad: exactamente lo que guarda la instrumentación.</summary>
    private static AuditSession Session(int daysAgo, int maxPasses, params decimal[] perUnitCosts)
    {
        var s = new AuditSession
        {
            Id = Ulids.NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "m",
            StartedUtc = Day.AddDays(-daysAgo),
            EndedUtc = Day.AddDays(-daysAgo),
            MaxPassesPerUnit = maxPasses,
            CycleN = 1,
        };

        for (int i = 0; i < perUnitCosts.Length; i++)
        {
            s.UsageBreakdown.Add(new UnitUsageBreakdown { Unit = $"U{i}.cs", Cost = perUnitCosts[i] });
        }

        return s;
    }

    // ---------------------------------------------------------------- con historial

    [Fact]
    public void Con_historial_la_media_sale_de_las_unidades_medidas()
    {
        var sessions = new[]
        {
            Session(2, maxPasses: 5, 10m, 20m),
            Session(1, maxPasses: 5, 30m, 20m),
        };

        CostEstimate e = CostEstimator.Estimate(sessions, units: 47, maxPasses: 5);

        e.Evidence.Should().Be(CostEvidence.Suficiente);
        e.CostPerUnit.Should().Be(20m, "(10 + 20 + 30 + 20) / 4");
        e.SampleUnits.Should().Be(4);
        e.SampleSessions.Should().Be(2);
        e.PassFactor.Should().Be(1m, "se midieron con el mismo tope que el vigente");
        e.Total.Should().Be(940m, "47 × 20");
    }

    [Fact]
    public void El_desglose_enseña_los_factores_no_solo_el_total()
    {
        var sessions = new[] { Session(1, maxPasses: 5, 18m, 18m, 18m) };

        CostEstimate e = CostEstimator.Estimate(sessions, units: 47, maxPasses: 5);

        // El formato que pide F5.6 §4: «47 unidades × ~18/unidad ≈ 846 unidades SDK».
        e.Breakdown.Should().StartWith("47 unidades × ~18/unidad")
            .And.Contain("846")
            .And.EndWith(CostEstimator.DefaultCostUnit);
    }

    [Fact]
    public void La_unidad_de_coste_es_la_que_declaro_el_SDK_no_una_inventada()
    {
        AuditSession s = Session(1, maxPasses: 5, 4m, 4m, 4m);
        s.Usage.Currency = "premium requests";

        CostEstimate e = CostEstimator.Estimate(new[] { s }, units: 10, maxPasses: 5);

        e.CostUnit.Should().Be("premium requests");
        e.Breakdown.Should().EndWith("premium requests");
    }

    [Fact]
    public void Solo_entran_las_sesiones_mas_recientes()
    {
        var many = Enumerable.Range(0, CostEstimator.RecentSessions + 4)
            .Select(i => Session(daysAgo: i, maxPasses: 5, i == 0 ? 100m : 10m))
            .ToArray();

        CostEstimate e = CostEstimator.Estimate(many, units: 1, maxPasses: 5);

        e.SampleSessions.Should().Be(CostEstimator.RecentSessions);
        e.SampleUnits.Should().Be(CostEstimator.RecentSessions);
    }

    // ---------------------------------------------------------------- sin historial

    [Fact]
    public void Sin_ninguna_sesion_no_se_estima_nada_y_se_dice()
    {
        CostEstimate e = CostEstimator.Estimate(Array.Empty<AuditSession>(), units: 47, maxPasses: 5);

        e.Evidence.Should().Be(CostEvidence.Ninguna);
        e.Total.Should().BeNull("no hay tarifa que aplicar: o hay medida o no hay número");
        e.HasNumber.Should().BeFalse();
        e.Provenance.Should().Contain("Sin coste medido");
        e.Breakdown.Should().Contain("47 unidades").And.Contain("desconocido");
    }

    [Fact]
    public void Una_sesion_sin_coste_medido_no_cuenta_como_historial()
    {
        // Sesión real pero sin desglose de coste: es lo que hay en las sesiones anteriores a Hito 1a.
        var legacy = new AuditSession
        {
            Id = Ulids.NewUlid(), AppSlug = "app", Mode = AuditMode.Integral,
            By = "a", Machine = "m", StartedUtc = Day, CycleN = 1,
        };

        CostEstimator.Estimate(new[] { legacy }, units: 5, maxPasses: 5)
            .Evidence.Should().Be(CostEvidence.Ninguna);
    }

    [Fact]
    public void Con_poco_historial_se_usa_el_ultimo_valor_conocido_y_se_declara()
    {
        var sessions = new[] { Session(1, maxPasses: 5, 12m) };

        CostEstimate e = CostEstimator.Estimate(sessions, units: 20, maxPasses: 5);

        e.Evidence.Should().Be(CostEvidence.Escasa);
        e.CostPerUnit.Should().Be(12m);
        e.Total.Should().Be(240m, "sigue siendo un número útil, solo que flojo");
        e.Provenance.Should().StartWith("Estimación con pocos datos")
            .And.Contain("1 unidad medida")
            .And.Contain("la última sesión");
    }

    // ---------------------------------------------------------------- factor de pasadas

    [Fact]
    public void Si_el_tope_vigente_no_es_el_medido_se_escala_y_se_enseña_el_factor()
    {
        var sessions = new[] { Session(1, maxPasses: 2, 10m, 10m, 10m) };

        CostEstimate e = CostEstimator.Estimate(sessions, units: 4, maxPasses: 6);

        e.ObservedMaxPasses.Should().Be(2);
        e.PassFactor.Should().Be(3m, "6 / 2");
        e.Total.Should().Be(120m, "4 × 10 × 3");
        e.Breakdown.Should().Contain("× 6/2 pasadas", "el factor se ve, no se esconde en el total");
        e.Provenance.Should().Contain("tope de 2 pasadas").And.Contain("sobreestima");
    }

    /// <summary>
    /// Cuando hay medidas tomadas con el MISMO tope, se prefieren: no hace falta extrapolar nada,
    /// y una extrapolación evitable es una fuente de error gratuita.
    /// </summary>
    [Fact]
    public void Se_prefieren_las_sesiones_medidas_con_el_tope_vigente()
    {
        var sessions = new[]
        {
            Session(1, maxPasses: 1, 100m, 100m, 100m),   // la más reciente, con otro tope
            Session(3, maxPasses: 5, 7m, 7m, 7m),         // más vieja, pero con el tope vigente
        };

        CostEstimate e = CostEstimator.Estimate(sessions, units: 10, maxPasses: 5);

        e.PassFactor.Should().Be(1m);
        e.CostPerUnit.Should().Be(7m);
        e.Total.Should().Be(70m);
    }

    [Fact]
    public void Un_historial_sin_tope_registrado_no_se_escala_y_lo_declara()
    {
        // MaxPassesPerUnit = 0 es lo que llevan las sesiones anteriores a F5.1.
        var sessions = new[] { Session(1, maxPasses: 0, 5m, 5m, 5m) };

        CostEstimate e = CostEstimator.Estimate(sessions, units: 10, maxPasses: 5);

        e.ObservedMaxPasses.Should().Be(0);
        e.PassFactor.Should().Be(1m);
        e.Total.Should().Be(50m);
        e.Provenance.Should().Contain("no registra con qué tope");
        e.Breakdown.Should().NotContain("pasadas", "no hay factor que enseñar");
    }

    [Fact]
    public void Un_tope_absurdo_se_trata_como_uno()
    {
        CostEstimate e = CostEstimator.Estimate(new[] { Session(1, 1, 3m, 3m, 3m) }, units: 2, maxPasses: 0);

        e.MaxPasses.Should().Be(1);
        e.Total.Should().Be(6m);
    }
}
