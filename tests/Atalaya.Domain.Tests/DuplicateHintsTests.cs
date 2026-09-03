using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

/// <summary>
/// F23 §5 — POSIBLES DUPLICADOS: SE MARCAN, NO SE FUSIONAN.
/// <para>
/// El caso de referencia tiene 25 hallazgos y unos 20 defectos: el auditor describe el mismo
/// problema dos veces con palabras distintas en pasadas distintas. Quien conoce el código lo ve;
/// quien lee para decidir, cuenta 25.
/// </para>
/// <para>
/// El criterio se eligió MIDIENDO contra ese caso, no a ojo — la tabla está en
/// <see cref="DuplicateHints"/>. Lo que fija esta suite es el contrato de cada pieza del criterio,
/// para que nadie lo afloje sin ver lo que se lleva por delante.
/// </para>
/// </summary>
public sealed class DuplicateHintsTests
{
    /// <summary>
    /// El par positivo del caso real: misma regla, mismo método, misma línea, dos redacciones.
    /// </summary>
    [Fact]
    public void Mismo_sitio_y_misma_regla_es_un_posible_duplicado()
    {
        Finding a = New("errores.concurrencia.race", "ClienteRemoto.cs", 23, "EnviarParteAsync");
        Finding b = New("errores.concurrencia.race", "ClienteRemoto.cs", 23, "ClienteRemoto.EnviarParteAsync");

        DuplicateHints.AreSimilar(a, b).Should().BeTrue(
            "el auditor cualifica el símbolo unas veces sí y otras no: es el mismo miembro");
    }

    /// <summary>A cinco líneas todavía es el mismo defecto: es lo que se midió.</summary>
    [Fact]
    public void Dentro_de_la_distancia_medida_tambien()
        => DuplicateHints.AreSimilar(
                New("errores.async.mal-usado", "ClienteRemoto.cs", 30, "DescargarPlantilla"),
                New("errores.async.mal-usado", "ClienteRemoto.cs", 33, "DescargarPlantilla"))
            .Should().BeTrue();

    // ------------------------------------------------------------------ los negativos

    /// <summary>Regla distinta: son dos defectos, aunque estén encima del otro.</summary>
    [Fact]
    public void Con_reglas_distintas_no_se_marca()
        => DuplicateHints.AreSimilar(
                New("criterio.rendimiento", "ClienteRemoto.cs", 14, "Http"),
                New("criterio.arquitectura", "ClienteRemoto.cs", 16, "Http"))
            .Should().BeFalse("un Timeout ausente y un CancellationToken ausente no son el mismo hallazgo");

    /// <summary>Demasiado lejos: la regla se repite en la unidad, no es que se repita el defecto.</summary>
    [Fact]
    public void Fuera_de_la_distancia_no_se_marca()
        => DuplicateHints.AreSimilar(
                New("errores.calculo.negocio", "CalculadoraCarga.cs", 11, "CargaTotalKg"),
                New("errores.calculo.negocio", "CalculadoraCarga.cs", 33, "CargaTotalKg"))
            .Should().BeFalse();

    /// <summary>
    /// <b>Miembros distintos, aunque caigan cerca.</b> Es lo que hace el trabajo de verdad: sin
    /// mirar el símbolo, «división por cero en CargaMediaPorMetro» (28) y «división por cero en
    /// CargaEspecifica» (33) se marcarían como el mismo defecto, y son dos métodos.
    /// </summary>
    [Fact]
    public void En_metodos_distintos_no_se_marca_aunque_esten_cerca()
        => DuplicateHints.AreSimilar(
                New("errores.calculo.negocio", "CalculadoraCarga.cs", 28, "CargaMediaPorMetro"),
                New("errores.calculo.negocio", "CalculadoraCarga.cs", 33, "CargaEspecifica"))
            .Should().BeFalse();

    /// <summary>
    /// Y un símbolo que es la CLASE no vale como señal: solo dice «en algún sitio de este fichero».
    /// Medido: era la mitad de las marcas falsas del caso de referencia.
    /// </summary>
    [Fact]
    public void Un_simbolo_de_clase_no_basta_para_marcar()
        => DuplicateHints.AreSimilar(
                New("criterio.seguridad", "ClienteRemoto.cs", 25, "ClienteRemoto"),
                New("criterio.seguridad", "ClienteRemoto.cs", 27, "ClienteRemoto"))
            .Should().BeFalse("«en ClienteRemoto.cs» no localiza nada");

    [Fact]
    public void Sin_simbolo_no_se_marca()
        => DuplicateHints.AreSimilar(
                New("criterio.seguridad", "A.cs", 10, null),
                New("criterio.seguridad", "A.cs", 10, null))
            .Should().BeFalse("sin dónde, no hay con qué comparar");

    [Fact]
    public void En_ficheros_distintos_no_se_marca()
        => DuplicateHints.AreSimilar(
                New("errores.null.desreferencia", "A.cs", 10, "Metodo"),
                New("errores.null.desreferencia", "B.cs", 10, "Metodo"))
            .Should().BeFalse();

    // ------------------------------------------------------------------ el conjunto

    /// <summary>
    /// Se marca el SEGUNDO de cada par —el que el lector encuentra ya sabiendo del primero— y una
    /// sola vez: encadenar marcas convierte una pista en un grafo.
    /// </summary>
    [Fact]
    public void Se_marca_el_segundo_y_solo_una_vez()
    {
        Finding primero = New("errores.calculo.negocio", "A.cs", 33, "CargaEspecifica");
        Finding segundo = New("errores.calculo.negocio", "A.cs", 33, "CargaEspecifica");
        Finding tercero = New("errores.calculo.negocio", "A.cs", 33, "CargaEspecifica");

        IReadOnlyDictionary<Ulid, Finding> hints =
            DuplicateHints.Of(new[] { primero, segundo, tercero });

        hints.Should().NotContainKey(primero.Id, "el primero no duplica a nadie");
        hints[segundo.Id].Should().Be(primero);
        hints[tercero.Id].Should().Be(primero, "siempre contra el primero al que se parece");
    }

    [Fact]
    public void Sin_parecidos_no_hay_marcas()
        => DuplicateHints.Of(new[]
            {
                New("errores.calculo.negocio", "A.cs", 11, "Uno"),
                New("criterio.seguridad", "A.cs", 40, "Dos"),
            })
            .Should().BeEmpty();

    private static int _seq;

    private static Finding New(string ruleId, string path, int line, string? symbol) => new()
    {
        Id = Ulid.Create(DateTimeOffset.UtcNow, stackalloc byte[10] { 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)++_seq }),
        RuleId = ruleId,
        Title = $"{ruleId} en {path}:{line}",
        Symbol = symbol,
        Severity = Severity.Media,
        Locations = new List<Location> { new(path, line, null) },
        FirstDetected = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "c", "quien"),
        LastConfirmed = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "c", "quien"),
    };
}
