using Atalaya.Agents;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// El re-anclaje de F5.6 (defectos 1 y 2), sobre el caso real que abrió el parte.
/// <para>
/// El fichero de abajo es una copia reducida y fiel de <c>CommonStatics.cs</c> del repositorio
/// X-BLAST: documentación XML encima de cada método y el código dentro. El hallazgo guardaba la
/// línea del <c>&lt;param&gt;</c> y la ficha resaltaba ese comentario.
/// </para>
/// </summary>
public sealed class AnchoringTests : IDisposable
{
    private readonly List<string> _temps = new();

    public void Dispose()
    {
        foreach (string dir in _temps)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Réplica del caso del usuario. Las líneas importan:
    /// 8 = <c>/// &lt;param name="detId"&gt;</c> (lo que guardaba el hallazgo),
    /// 10 = la declaración de <c>ConvertToDetId</c>, 12 = <c>return uint.Parse(...)</c>.
    /// </summary>
    private const string Fuente = """
        namespace XBLASTCommon;

        public static class CommonStatics
        {
            /// <summary>
            /// Parsea un arreglo de bytes en una cadena hexadecimal.
            /// </summary>
            /// <param name="detId">El identificador de detonador en formato hexadecimal.</param>
            /// <returns>El identificador de detonador como un uint.</returns>
            public static uint ConvertToDetId(string detId)
            {
                return uint.Parse(detId, NumberStyles.HexNumber);
            }

            /// <summary>Parsea una cadena en un número de secuencia.</summary>
            /// <param name="seq">La cadena que representa el número de secuencia.</param>
            public static ushort ConvertToSeq(string seq)
            {
                return ushort.Parse(seq);
            }
        }
        """;

    private const int LineaDelComentario = 8;
    private const int LineaDeclaracion = 10;
    private const int LineaDelCodigo = 12;

    private static string[] Lineas() => Fuente.Replace("\r\n", "\n").Split('\n');

    // ================================================ el hash es el ancla, no el número de línea

    /// <summary>
    /// El corazón del defecto 1 (D-217/D-221). El hallazgo guarda el hash del snippet <b>sin
    /// sangría</b> que mandó el LLM y una línea equivocada; con el hash simétrico, la búsqueda
    /// encuentra la línea buena sola. Esto es lo que arregla 53 de las 56 ubicaciones reales.
    /// </summary>
    [Fact]
    public void El_hash_encuentra_la_linea_real_aunque_el_numero_guardado_este_mal()
    {
        string hash = CodeAnchor.ComputeSnippetHash("return uint.Parse(detId, NumberStyles.HexNumber);");

        LocationAnchor.FindByHash(Lineas(), hash, LineaDelComentario).Should().Be(LineaDelCodigo);
    }

    /// <summary>
    /// Como el hash ya ignora la sangría, dos líneas idénticas con sangrías distintas casan las
    /// dos. Se elige la más cercana a la línea guardada, no la primera del fichero (D-219).
    /// </summary>
    [Fact]
    public void Entre_varias_candidatas_gana_la_mas_cercana_a_la_linea_guardada()
    {
        string[] lines = { "return x;", "a();", "b();", "c();", "    return x;" };
        string hash = CodeAnchor.ComputeSnippetHash("return x;");

        LocationAnchor.FindByHash(lines, hash, 4).Should().Be(5);
        LocationAnchor.FindByHash(lines, hash, 2).Should().Be(1);
    }

    [Fact]
    public void Un_hash_que_no_esta_no_devuelve_ninguna_linea()
        => LocationAnchor.FindByHash(Lineas(), CodeAnchor.ComputeSnippetHash("no existe"), 1)
            .Should().Be(0);

    // ================================================================== re-anclaje por símbolo

    /// <summary>
    /// El símbolo lleva al <b>código</b> del método, no a su documentación ni a su firma: la línea
    /// buena es el primer statement ejecutable (D-224).
    /// </summary>
    [Fact]
    public void El_simbolo_ancla_al_primer_codigo_del_miembro_nunca_al_comentario()
    {
        SymbolHit hit = SymbolAnchor.FindMember(Lineas(), "CommonStatics.cs", new[] { "ConvertToDetId" });

        hit.Found.Should().BeTrue();
        hit.Line.Should().Be(LineaDelCodigo);
        hit.Member.Should().Be("CommonStatics.ConvertToDetId");
    }

    /// <summary>Un símbolo renombrado o eliminado no se inventa: no está y punto (D-225).</summary>
    [Fact]
    public void Un_simbolo_que_ya_no_existe_no_devuelve_nada()
        => SymbolAnchor.FindMember(Lineas(), "CommonStatics.cs", new[] { "ConvertToDetIdViejo" })
            .Found.Should().BeFalse();

    [Fact]
    public void Fuera_de_C_sharp_no_se_intenta_anclar_por_simbolo()
        => SymbolAnchor.FindMember(Lineas(), "script.py", new[] { "ConvertToDetId" })
            .Found.Should().BeFalse();

    // ============================================================ nunca se resalta un comentario

    /// <summary>
    /// El síntoma exacto del parte: la línea guardada es un <c>&lt;param&gt;</c> de documentación.
    /// Se baja al primer código del miembro que la documenta.
    /// </summary>
    [Fact]
    public void Una_linea_de_documentacion_se_corrige_al_codigo_del_miembro()
        => SymbolAnchor.FirstCodeLine(Lineas(), "CommonStatics.cs", LineaDelComentario)
            .Should().Be(LineaDelCodigo);

    /// <summary>La firma tampoco es el resaltado bueno si el miembro tiene cuerpo.</summary>
    [Fact]
    public void La_declaracion_del_miembro_tambien_baja_a_su_primer_codigo()
        => SymbolAnchor.FirstCodeLine(Lineas(), "CommonStatics.cs", LineaDeclaracion)
            .Should().Be(LineaDelCodigo);

    /// <summary>Y una línea que ya era código se queda donde está: corregir de más es mentir.</summary>
    [Fact]
    public void Una_linea_que_ya_es_codigo_no_se_toca()
        => SymbolAnchor.FirstCodeLine(Lineas(), "CommonStatics.cs", LineaDelCodigo)
            .Should().Be(LineaDelCodigo);

    // ====================================================== de qué símbolos dispone un hallazgo

    /// <summary>
    /// El <c>symbol</c> declarado manda, y de él se prefiere la parte más específica: el miembro
    /// antes que la clase.
    /// </summary>
    [Fact]
    public void El_simbolo_declarado_va_primero_y_el_miembro_antes_que_la_clase()
        => SymbolAnchor.Candidates("CommonStatics.ConvertToDetId", "Algo")
            .Should().StartWith(new[] { "ConvertToDetId", "CommonStatics" });

    /// <summary>
    /// Sin <c>symbol</c> —los hallazgos anteriores a D-223— los candidatos salen del título. El
    /// título real del parte da los dos métodos y deja fuera las palabras en castellano.
    /// </summary>
    [Fact]
    public void Sin_simbolo_los_candidatos_salen_del_titulo()
    {
        IReadOnlyList<string> c = SymbolAnchor.Candidates(
            null, "ConvertToDetId/ConvertToSeq propagan excepciones no controladas de Parse");

        c.Should().StartWith(new[] { "ConvertToDetId", "ConvertToSeq" });
        c.Should().NotContain("propagan");
        c.Should().NotContain("excepciones");
    }

    // ============================================================ el ancla se corrige al persistir

    /// <summary>
    /// D-226: lo que se guarda al ingerir es la línea <b>real</b>, no la aproximada que dijo el
    /// auditor. Si no fuera así, el arreglo sería solo cosmético y cada detección nueva volvería a
    /// nacer torcida.
    /// </summary>
    [Fact]
    public void Al_persistir_se_guarda_la_linea_real_y_no_la_que_dijo_el_auditor()
    {
        string clone = NewClone(out string rel);

        int line = LocationAnchor.ResolveOnDisk(
            clone, rel, LineaDelComentario,
            CodeAnchor.ComputeSnippetHash("return uint.Parse(detId, NumberStyles.HexNumber);"));

        line.Should().Be(LineaDelCodigo);
    }

    /// <summary>Sin clon, sin fichero o sin snippet no se corrige nada: solo lo comprobable.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Lo_que_no_se_puede_comprobar_se_guarda_tal_cual(bool sinClon, bool ficheroInexistente)
    {
        string clone = NewClone(out string rel);
        string hash = CodeAnchor.ComputeSnippetHash("return uint.Parse(detId, NumberStyles.HexNumber);");

        int line = LocationAnchor.ResolveOnDisk(
            sinClon ? null : clone,
            ficheroInexistente ? "src/NoExiste.cs" : rel,
            LineaDelComentario,
            hash);

        line.Should().Be(LineaDelComentario);
    }

    [Fact]
    public void Sin_hash_no_hay_nada_con_lo_que_re_anclar()
    {
        string clone = NewClone(out string rel);

        LocationAnchor.ResolveOnDisk(clone, rel, LineaDelComentario, null)
            .Should().Be(LineaDelComentario);
    }

    // ================================ BUGFIX-ANCLA: nunca nace anclado a una llave

    /// <summary>
    /// <b>La pieza común: la ubicación se baja a la primera línea ejecutable de su miembro, y el
    /// hash se recalcula sobre ESA</b> (BUGFIX-ANCLA §1.3).
    /// <para>
    /// D-226 corregía al ingerir <b>solo</b> el caso (1) —buscar el snippet—; cuando el snippet no
    /// aparecía se guardaba el número que dijo el LLM tal cual, y el caso (2) vivía únicamente al
    /// abrir la ficha. Medido en el hub de xblast: <b>69 de 398</b> ubicaciones tenían como línea
    /// del hallazgo una llave, un comentario, un atributo o un blanco. Cebo: con el ingest anterior
    /// —que no llamaba a esto— la línea guardada era la que llegó.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(8)]     // un comentario de documentación
    [InlineData(11)]    // la llave de apertura del cuerpo
    [InlineData(13)]    // la llave de cierre
    public void Una_linea_no_ejecutable_se_baja_al_codigo_del_miembro_al_ingerir(int reportada)
    {
        string clone = NewClone(out string rel);

        Location loc = LocationAnchor.OnFirstCodeLine(clone, rel, reportada, snippetHash: "sha256:loquesea");

        loc.Line.Should().Be(LineaDelCodigo, "la primera línea ejecutable del miembro (D-224)");
        loc.SnippetHash.Should().Be(
            CodeAnchor.ComputeSnippetHash("        return uint.Parse(detId, NumberStyles.HexNumber);"),
            "y el ancla se calcula sobre la línea que se guarda, no sobre la que llegó");
    }

    /// <summary>
    /// Y es idempotente y prudente: una línea que ya es código se guarda tal cual, y sin clon o sin
    /// fichero tampoco se toca nada — el mismo criterio de <c>ResolveOnDisk</c>.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Lo_que_ya_es_codigo_o_no_se_puede_comprobar_se_guarda_tal_cual(bool sinClon, bool sinFichero)
    {
        string clone = NewClone(out string rel);
        const string hash = "sha256:loquesea";

        Location loc = LocationAnchor.OnFirstCodeLine(
            sinClon ? null : clone,
            sinFichero ? "src/NoExiste.cs" : rel,
            LineaDelCodigo,
            hash);

        loc.Line.Should().Be(LineaDelCodigo);
        loc.SnippetHash.Should().Be(hash, "no se recalcula un ancla que no se ha movido");
    }

    /// <summary>
    /// <b>Y los TRES caminos del ingest pasan por ahí</b>: <c>submit_finding</c>,
    /// <c>submit_findings</c> y <c>add_locations</c>. Se ejercitan los tres de verdad contra un
    /// clon, porque el defecto era que uno de ellos guardara el número crudo.
    /// </summary>
    [Fact]
    public void Los_tres_caminos_del_ingest_guardan_la_primera_linea_ejecutable()
    {
        string clone = NewClone(out string rel);
        string root = Path.Combine(clone, "hub");
        var paths = new AppPaths(root);
        var settings = new SettingsService(paths);
        settings.Load();
        HubContext hub = TestFactory.Hub(paths, settings);
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "https://x/y.git", CurrentCycle = 1 });

        var ulids = new UlidFactory(SystemClock.Instance);
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "auditor");
        var toolbox = new SessionToolbox(
            "app", AuditMode.Lotes, stamp,
            new FindingIngestionService(hub, ulids),
            new ReconciliationService(hub),
            hub.Store, clone);

        // (1) submit_finding, con la línea del comentario.
        SubmitFindingResult uno = toolbox.SubmitFinding(Payload("BUG uno", rel, LineaDelComentario));
        uno.Accepted.Should().BeTrue(
            uno.Error ?? string.Join(" · ", toolbox.RejectedPayloads));

        // (2) submit_findings, con la llave de cierre.
        toolbox.SubmitFindings(new[] { Payload("BUG dos", rel, 13) })
            .Results.Should().OnlyContain(r => r.Accepted);

        List<Finding> stored = hub.Store.ListFindings("app").ToList();
        stored.Should().HaveCount(2);
        stored.Should().OnlyContain(f => f.Locations[0].Line == LineaDelCodigo,
            "ni un comentario ni una llave sobreviven como línea del hallazgo");
        stored.Should().OnlyContain(f =>
                f.Locations[0].SnippetHash
                    == CodeAnchor.ComputeSnippetHash("        return uint.Parse(detId, NumberStyles.HexNumber);"),
            "y el hash es el de la línea guardada");

        // (3) add_locations sobre uno de ellos, con la llave de apertura.
        Finding target = stored[0];
        toolbox.AddLocations(
            target.Id.ToString(),
            new[] { new SubmitLocation(rel, 11, null) })
            .Accepted.Should().BeTrue();

        Finding after = hub.Store.TryReadFinding("app", target.Id.ToString())!;
        after.Locations.Should().HaveCountGreaterThan(1);
        after.Locations[^1].Line.Should().Be(LineaDelCodigo, "add_locations también");
    }

    private static SubmitFindingArgs Payload(string title, string path, int line)
        => new(
            RuleId: "criterio.validacion",
            Pillar: "errores",
            Severity: "alta",
            Title: title,
            Description: "El identificador no se valida.",
            Impact: "Excepción no controlada.",
            Recommendation: "Validar antes de parsear.",
            Locations: new[] { new SubmitLocation(path, line, null) },
            Symbol: "ConvertToDetId");

    // ================================================================== la rueda del ratón (§4)

    /// <summary>
    /// Las cuatro esquinas del defecto 4 (D-230). En el tope y empujando hacia fuera, la rueda es
    /// de la página; con recorrido por delante, es del snippet.
    /// </summary>
    [Theory]
    [InlineData(120, 0, 100, 500, true)]      // arriba del todo, subiendo → la página
    [InlineData(-120, 400, 100, 500, true)]   // abajo del todo, bajando → la página
    [InlineData(-120, 0, 100, 500, false)]    // arriba del todo, bajando → el snippet
    [InlineData(120, 400, 100, 500, false)]   // abajo del todo, subiendo → el snippet
    public void La_rueda_burbujea_solo_cuando_el_snippet_ya_no_puede_desplazarse(
        int delta, double offset, double viewport, double extent, bool burbujea)
        => SnippetScroll.ShouldBubble(delta, offset, viewport, extent).Should().Be(burbujea);

    /// <summary>
    /// Un método corto cabe entero y no tiene nada que desplazar: la página tiene que bajar
    /// fluida al pasar por encima, en las dos direcciones.
    /// </summary>
    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    public void Un_metodo_que_cabe_entero_nunca_se_queda_la_rueda(int delta)
        => SnippetScroll.ShouldBubble(delta, 0, 320, 180).Should().BeTrue();

    private string NewClone(out string relativePath)
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        _temps.Add(root);
        relativePath = "src/CommonStatics.cs";
        File.WriteAllText(Path.Combine(root, "src", "CommonStatics.cs"), Fuente);
        return root;
    }
}
