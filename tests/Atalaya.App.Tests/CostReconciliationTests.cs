using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F29 — <b>el coste que faltaba</b>: las sesiones que se quedaron sin valorar, y las tres formas
/// de cerrar su hueco.
/// <para>
/// Son tests de REGLA (N-5, N-7): cada uno protege algo que se rompería en silencio — un agregado
/// que sigue diciendo «parcial» cuando ya no falta nada, un coste estimado que se enseña como si
/// se hubiera medido, o un informe inmutable que alguien reescribe al reconciliar.
/// </para>
/// </summary>
public sealed class CostReconciliationTests : IDisposable
{
    private const string Slug = "app";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly ModelRatesService _rates;
    private readonly CostReconciliationService _reconciler;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public CostReconciliationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f29", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteApp(new AppConfig { Slug = Slug, Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        _rates = new ModelRatesService(_hub);
        _reconciler = new CostReconciliationService(_hub, _rates);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ============================================================ §0 · el diagnóstico

    /// <summary>
    /// <b>D-788 sigue en pie tras R2</b> (§0): añadir una tarifa recalcula sola una sesión vieja,
    /// sin migrar nada y sin volver a escribirla. El coste es un DERIVADO, y por eso lo que queda
    /// sin valorar son solo las sesiones cuyo modelo no puede tener tarifa.
    /// </summary>
    [Fact]
    public void Anadir_una_tarifa_recalcula_sola_una_sesion_vieja()
    {
        AuditSession vieja = Session("un-modelo-nuevo", outputTokens: 2_000);
        _hub.Store.WriteSession(vieja);

        _rates.CostOf(vieja).Why.Should().Be(CostUnavailable.RateMissing);

        _rates.Save(new ModelRateTable { Rates = { new ModelRate("un-modelo-nuevo", string.Empty, 0m, 10m, 0m) } });

        // Ni se ha tocado el fichero de la sesión ni se ha reconciliado nada: la fórmula la
        // recalcula al leerla.
        _rates.CostOf(Reload(vieja.Id)).Usd.Should().Be(0.02m);
    }

    /// <summary>
    /// <b>«auto» no es un modelo sin tarifa: es un modelo desconocido</b> (§0). Ninguna tarifa lo
    /// cubre por muchas que se añadan, así que pedir que se configure su precio manda a nadie a
    /// ninguna parte — y era exactamente lo que decía el aviso ámbar: «auto (copilot) · 1 sesión».
    /// </summary>
    [Fact]
    public void Auto_no_se_lista_como_modelo_al_que_le_falta_una_tarifa()
    {
        _rates.Save(TestRates.Table());
        _hub.Store.WriteSession(Session(ModelIds.Auto, outputTokens: 1_000));

        _rates.ModelsWithoutRate().Should().BeEmpty(
            "no hay ningún precio publicado para «lo que el enrutador decida»");

        _reconciler.GapOf(Slug).Groups.Should().ContainSingle()
            .Which.Reason.Should().Be(CostGapReason.Desconocido);
    }

    // ============================================================ §1 · reconciliar

    /// <summary>
    /// <b>El caso completo del encargo</b>: una sesión sin tarifa para su modelo, la tarifa añadida,
    /// reconciliar — y el «parcial» fuera.
    /// </summary>
    [Fact]
    public async Task Sin_tarifa_mas_tarifa_anadida_mas_reconciliar_da_coste_y_quita_el_parcial()
    {
        _rates.Save(TestRates.Table());
        AuditSession sesion = Session("modelo-raro", outputTokens: 3_000);
        _hub.Store.WriteSession(sesion);

        _reconciler.GapOf(Slug).Sessions.Should().Be(1, "está sin coste y se dice");

        // El diálogo se abre: es donde el usuario ve el grupo y desde donde va a las tarifas.
        var dialogo = new ReconcileCostsViewModel(_reconciler, _rates);
        dialogo.Load(Slug, "App");
        dialogo.Groups.Should().ContainSingle().Which.NeedsRate.Should().BeTrue();

        // La tarifa entra en la tabla del hub, que es donde se editan los precios.
        ModelRateTable table = _rates.Current!;
        table.Rates.Add(new ModelRate("modelo-raro", string.Empty, 0m, 10m, 0m));
        _rates.Save(table);

        // Al volver, el grupo NO desaparece: pasa a «listo para calcular».
        dialogo.Refresh();
        ReconcileGroupRow grupo = dialogo.Groups.Should().ContainSingle().Subject;
        grupo.IsReady.Should().BeTrue();
        dialogo.ReconcileLabel.Should().Be("Reconciliar 1 sesión");

        await dialogo.ReconcileCommand.ExecuteAsync(null);

        _reconciler.GapOf(Slug).Sessions.Should().Be(0,
            "ya no falta ninguna: el agregado deja de ser parcial");
        _reconciler.LookupFor(Slug).Of(Reload(sesion.Id)).Usd.Should().Be(0.03m);
    }

    /// <summary>
    /// <b>Lo medido gana</b> (§0): si las llamadas guardaron con qué modelo contestó cada una y sus
    /// tokens son los de la sesión, el coste se calcula llamada a llamada. Es una MEDIDA, así que
    /// no lleva marca de estimado — y no hace falta que nadie elija ninguna tarifa.
    /// </summary>
    [Fact]
    public void Con_el_modelo_real_en_las_llamadas_auto_se_valora_por_llamada_y_sin_marca()
    {
        _rates.Save(new ModelRateTable
        {
            Rates = { new ModelRate("modelo-a", string.Empty, 0m, 10m, 0m), new ModelRate("modelo-b", string.Empty, 0m, 20m, 0m) },
        });

        AuditSession sesion = Session(ModelIds.Auto, outputTokens: 3_000);
        sesion.UsageBreakdown.Add(new UnitUsageBreakdown
        {
            Unit = "u",
            Samples =
            {
                new CallSample(1, 0, 1_000, 0, 0, null, "modelo-a"),
                new CallSample(2, 0, 2_000, 0, 0, null, "modelo-b"),
            },
        });
        _hub.Store.WriteSession(sesion);

        _reconciler.Reconcile(Slug).Sessions.Should().Be(1);

        CostResult cost = _reconciler.LookupFor(Slug).Of(Reload(sesion.Id));

        // 1.000 a 10 $/M = 0,01 $ = 1 credit; 2.000 a 20 $/M = 0,04 $ = 4 credits.
        cost.Usd.Should().Be(0.05m);
        cost.IsEstimate.Should().BeFalse("sumar lo que costó cada llamada no es estimar");
    }

    /// <summary>
    /// <b>Y lo que nadie midió se VALORA, con su marca</b> (§1): una sesión «auto» cuyas llamadas no
    /// dicen con qué modelo contestaron no tiene coste hasta que una persona elige con qué tarifa,
    /// y el número que sale lleva su marca de estimado — para siempre.
    /// </summary>
    [Fact]
    public void Auto_sin_modelo_real_solo_tiene_coste_tras_asignar_una_tarifa_y_queda_marcado()
    {
        _rates.Save(TestRates.Table());
        AuditSession sesion = Session(ModelIds.Auto, outputTokens: 4_000);
        _hub.Store.WriteSession(sesion);

        // Reconciliar sin elegir tarifa no cierra nada: inventar una «parecida» es lo que D-787
        // prohíbe.
        _reconciler.Reconcile(Slug).Sessions.Should().Be(0);
        _reconciler.GapOf(Slug).Sessions.Should().Be(1);

        _reconciler.Reconcile(Slug, TestRates.Model).Sessions.Should().Be(1);

        CostResult cost = _reconciler.LookupFor(Slug).Of(Reload(sesion.Id));
        cost.Usd.Should().Be(4m);
        cost.IsEstimate.Should().BeTrue("nadie midió con qué modelo corrió");
        cost.EstimatedWith!.AssignedModel.Should().Be(TestRates.Model);
        CostFormat.Marked("4,0", cost).Should().Be("4,0" + CostFormat.EstimateMark);
        CostFormat.EstimateTooltip(cost.EstimatedWith).Should()
            .Contain(TestRates.Model).And.Contain(cost.EstimatedWith.By);
    }

    /// <summary>
    /// <b>Un coste estimado no se convierte en medido.</b> Reconciliar otra vez —con otra tarifa
    /// elegida, incluso— no le quita la marca a lo que ya se valoró a mano: la marca se queda.
    /// </summary>
    [Fact]
    public void Reconciliar_otra_vez_no_convierte_un_estimado_en_medido()
    {
        _rates.Save(TestRates.Table());
        _hub.Store.WriteSession(Session(ModelIds.Auto, outputTokens: 1_000));
        _reconciler.Reconcile(Slug, TestRates.Model);

        _reconciler.Reconcile(Slug, TestRates.LegacyModel).Sessions.Should().Be(0, "ya no hay hueco que cerrar");

        AuditSession sesion = _hub.Store.ListSessions(Slug).Single();
        CostResult cost = _reconciler.LookupFor(Slug).Of(sesion);
        cost.IsEstimate.Should().BeTrue();
        cost.EstimatedWith!.AssignedModel.Should().Be(TestRates.Model, "manda la primera asignación");
    }

    /// <summary>
    /// <b>La sesión NO se toca</b>: la reconciliación vive en su propia carpeta del hub, como los
    /// arreglos de F9 §2. Una sesión es el registro inmutable de lo que pasó aquel día, y esto es
    /// una decisión posterior sobre cómo valorarla.
    /// </summary>
    [Fact]
    public void Reconciliar_escribe_en_su_carpeta_y_no_reescribe_la_sesion()
    {
        _rates.Save(TestRates.Table());
        AuditSession sesion = Session(ModelIds.Auto, outputTokens: 1_000);
        _hub.Store.WriteSession(sesion);

        string sessionFile = _hub.HubPaths.SessionFile(Slug, sesion.Id.ToString());
        string antes = File.ReadAllText(sessionFile);

        _reconciler.Reconcile(Slug, TestRates.Model);

        File.ReadAllText(sessionFile).Should().Be(antes, "la sesión es un registro inmutable");
        File.Exists(_hub.HubPaths.CostReconciliationFile(Slug, sesion.Id.ToString())).Should().BeTrue();
    }

    /// <summary>
    /// <b>Y NO se guarda un coste</b>, que es la trampa que D-788 fue a eliminar. Lo que se escribe
    /// es con qué valorar; corregir la tarifa después sigue cambiando el número, como en cualquier
    /// otra sesión.
    /// </summary>
    [Fact]
    public void La_reconciliacion_no_guarda_un_importe_y_la_tarifa_corregida_sigue_mandando()
    {
        _rates.Save(TestRates.Table());
        AuditSession sesion = Session(ModelIds.Auto, outputTokens: 1_000);
        _hub.Store.WriteSession(sesion);
        _reconciler.Reconcile(Slug, TestRates.Model);

        _reconciler.LookupFor(Slug).Of(Reload(sesion.Id)).Usd.Should().Be(1m);

        // La organización corrige el precio: el doble.
        _rates.Save(new ModelRateTable { Rates = { new ModelRate(TestRates.Model, string.Empty, 0m, 20m, 0m) } });

        _reconciler.LookupFor(Slug).Of(Reload(sesion.Id)).Usd.Should().Be(0.02m,
            "el coste sigue siendo un derivado que se recalcula en cada lectura (D-788)");
    }

    /// <summary>
    /// <b>Un informe ya escrito no cambia</b> (F23: el informe es lo que se vio ese día). Lo que se
    /// añade es la LÍNEA que dice cuándo se calculó el coste, y va fuera del documento.
    /// </summary>
    [Fact]
    public void Un_informe_ya_escrito_no_se_reescribe_al_reconciliar()
    {
        _rates.Save(TestRates.Table());
        AuditSession sesion = Session(ModelIds.Auto, outputTokens: 1_000);
        _hub.Store.WriteSession(sesion);
        _hub.Store.WriteReport(Slug, sesion.Id.ToString(),
            "# Informe de sesión — App\n\n- **Coste**: coste no calculable (modelo no registrado)\n");

        string antes = File.ReadAllText(_hub.HubPaths.ReportFile(Slug, sesion.Id.ToString()));

        _reconciler.Reconcile(Slug, TestRates.Model);

        File.ReadAllText(_hub.HubPaths.ReportFile(Slug, sesion.Id.ToString())).Should().Be(antes);

        // Y la lista de informes sí lo enseña calculado, con la fecha en que se cerró.
        ReportEntry fila = new ReportsQuery(_hub).All().Single();
        fila.Cost.Should().Be(1m);
        fila.CostCalculatedLater.Should().BeTrue();
        fila.CalculatedLaterLine.Should().StartWith("Coste calculado a posteriori el ");
    }

    /// <summary>
    /// <b>Lo que no es un hueco no cuenta</b> (D-787): una sesión sin tokens no tiene nada que
    /// valorar, así que ni sale en la insignia ni ensucia el aviso. Manchar el recuento con esas
    /// haría que se aprendiera a ignorarlo.
    /// </summary>
    [Fact]
    public void Una_sesion_sin_tokens_no_cuenta_como_sesion_sin_coste()
    {
        _rates.Save(TestRates.Table());
        _hub.Store.WriteSession(Session(ModelIds.Auto, outputTokens: 0));

        _reconciler.GapOf(Slug).Sessions.Should().Be(0);
    }

    // ================================================================ helpers

    private AuditSession Session(string model, long outputTokens) => new()
    {
        Id = _ulids.NewUlid(),
        AppSlug = Slug,
        By = "alopezciller",
        Machine = "PC",
        Mode = AuditMode.Lotes,
        Provider = "copilot",
        Model = model,
        CycleN = 1,
        StartedUtc = DateTimeOffset.UtcNow,
        Usage = new UsageTotals { OutputTokens = outputTokens, Calls = 1 },
    };

    private AuditSession Reload(Ulid id) => _hub.Store.ListSessions(Slug).Single(s => s.Id == id);
}
