using Atalaya.Copilot;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// BUGFIX-CUOTA — cada fallo del proveedor con su causa y su remedio.
/// <para>
/// El parte: al agotarse las peticiones premium de la organización, la sesión en vivo dijo en rojo
/// «tu cuenta no tiene asiento en GitHub». La causa era falsa y el remedio también —reclamar un
/// asiento que ya se tiene, en vez de esperar al reset—. El error REAL, copiado del log del
/// 2026-08-31 a las 08:17:31, es <see cref="RealQuotaError"/>: el SDK no tipa nada, así que
/// clasificar es leer un texto, y «quota» vivía dentro del detector de «sin asiento».
/// </para>
/// </summary>
public sealed class ProviderFailureTests
{
    /// <summary>El error tal cual lo escribió el runtime. Literal, sin retocar.</summary>
    private const string RealQuotaError =
        "Session error: You have exceeded your monthly quota "
        + "(Request ID: FA81:2498A4:244E7F9:2DAED35:6A951C79)";

    private static AgentProblem Of(string message, bool hasToken = true)
        => CopilotFailure.Classify(new InvalidOperationException(message), hasToken);

    // ============================================================ el fallo del parte

    [Fact]
    public void El_error_real_de_cuota_se_clasifica_como_cuota_y_jamas_como_asiento()
    {
        AgentProblem problem = Of(RealQuotaError);

        problem.Should().Be(AgentProblem.QuotaExhausted);
        problem.Should().NotBe(AgentProblem.NoSeat,
            "decirle a alguien que no tiene asiento cuando lo que falta son peticiones lo manda a "
            + "hablar con IT sobre algo que ya tiene");
    }

    [Fact]
    public void Y_su_mensaje_habla_de_peticiones_premium_y_no_de_asientos()
    {
        var ex = new InvalidOperationException(RealQuotaError);
        AgentReadiness bad = CopilotFailure.Diagnose(ex, hasToken: true);

        bad.Message.Should().Contain("peticiones premium");
        bad.Message.Should().NotContain("no tiene asiento",
            "el asiento está: culparlo manda a alguien a reclamar lo que ya tiene");
        bad.Message.Should().Contain("No es tu asiento",
            "y se descarta en voz alta, porque es el diagnóstico que la aplicación daba mal");
        bad.Message.Should().Contain("mensual", "el propio error dice «monthly», así que se puede decir");
        bad.Message.Should().Contain("esperar", "el remedio es esperar al reset, no tocar nada aquí");
    }

    [Fact]
    public void La_cuota_no_es_reintentable_y_se_declara()
        => CopilotFailure.IsRetryable(AgentProblem.QuotaExhausted).Should().BeFalse(
            "reintentar contra un grifo cerrado quema las peticiones del reset siguiente");

    [Fact]
    public void Lo_transitorio_si_es_reintentable()
        => CopilotFailure.IsRetryable(AgentProblem.Offline).Should().BeTrue();

    // ============================================================ la taxonomía entera

    [Theory]
    // Cuota / peticiones premium.
    [InlineData(RealQuotaError, AgentProblem.QuotaExhausted)]
    [InlineData("You have exceeded your premium request allowance", AgentProblem.QuotaExhausted)]
    [InlineData("Copilot usage limit reached for this organization", AgentProblem.QuotaExhausted)]
    [InlineData("HTTP 429: too many requests", AgentProblem.QuotaExhausted)]
    // Asiento, solo cuando el proveedor lo dice.
    [InlineData("The user has no Copilot seat assigned", AgentProblem.NoSeat)]
    [InlineData("user is not entitled to Copilot", AgentProblem.NoSeat)]
    [InlineData("copilot_not_enabled for this account", AgentProblem.NoSeat)]
    // Credenciales.
    [InlineData("HTTP 401: Bad credentials", AgentProblem.TokenRejected)]
    [InlineData("request unauthorized", AgentProblem.TokenRejected)]
    // Modelo.
    [InlineData("Model gpt-5 is not available", AgentProblem.ModelUnavailable)]
    [InlineData("session.create failed: unknown model", AgentProblem.ModelUnavailable)]
    // Red / servicio.
    [InlineData("No such host is known", AgentProblem.Offline)]
    [InlineData("HTTP 503: service unavailable", AgentProblem.Offline)]
    // Lo que no se reconoce NO se inventa.
    [InlineData("Session error: something nobody has seen before", AgentProblem.Unknown)]
    public void Cada_error_del_proveedor_cae_en_su_sitio(string message, AgentProblem expected)
        => Of(message).Should().Be(expected);

    [Fact]
    public void El_desconocido_ensena_el_error_crudo_en_vez_de_proponer_una_causa()
    {
        var ex = new InvalidOperationException("Session error: algo jamás visto (Request ID: AB:12)");
        AgentReadiness bad = CopilotFailure.Diagnose(ex, hasToken: true);

        bad.Problem.Should().Be(AgentProblem.Unknown);
        bad.Message.Should().Contain("no reconoce el motivo").And.Contain("no se lo inventa");
        bad.Message.Should().Contain("algo jamás visto", "el dato crudo antes que una causa inventada");
        bad.Detail.Should().Contain("InvalidOperationException").And.Contain("Request ID: AB:12");
    }

    [Fact]
    public void El_crudo_lleva_el_tipo_el_texto_y_el_request_id_para_poder_pegarlo()
    {
        string raw = CopilotFailure.Raw(new InvalidOperationException(RealQuotaError));

        raw.Should().StartWith("InvalidOperationException: ");
        raw.Should().Contain("FA81:2498A4:244E7F9:2DAED35:6A951C79",
            "es lo único con lo que quien administra la organización puede buscar la petición");
    }

    [Fact]
    public void Y_encadena_las_causas_cuando_el_SDK_envuelve_el_error()
        => CopilotFailure.Raw(new InvalidOperationException("fuera", new Exception("dentro")))
            .Should().Contain("fuera").And.Contain("dentro");

    // ============================================================ los matices que costaron el fallo

    /// <summary>
    /// El Request ID es hexadecimal separado por dos puntos y puede contener un 401 o un 403 por
    /// casualidad. Clasificar por él sería repetir el fallo con otro disfraz.
    /// </summary>
    [Fact]
    public void Un_codigo_http_dentro_del_request_id_no_clasifica_nada()
        => Of("Session error: You have exceeded your monthly quota (Request ID: 401:403:AA)")
            .Should().Be(AgentProblem.QuotaExhausted);

    /// <summary>Y un 403 dentro de una ristra hexadecimal tampoco es un 403.</summary>
    [Fact]
    public void Un_403_pegado_a_letras_no_es_un_codigo_http()
        => Of("Session error: reference F403A is malformed").Should().Be(AgentProblem.Unknown);

    /// <summary>
    /// Un 403 pelado no dice de qué va. Con credencial detrás la lectura más probable es el
    /// asiento, pero se declara que es una conjetura y el crudo viaja al lado para contradecirla.
    /// </summary>
    [Fact]
    public void Un_403_sin_palabras_es_conjetura_y_se_dice_que_lo_es()
    {
        var ex = new InvalidOperationException("HTTP 403");
        AgentReadiness bad = CopilotFailure.Diagnose(ex, hasToken: true);

        bad.Problem.Should().Be(AgentProblem.NoSeat);
        bad.Message.Should().Contain("solo ha devuelto un 403",
            "afirmar la causa sin que el proveedor la diga es lo que rompió esto");
    }

    /// <summary>Sin credencial no se conjetura un asiento: falta lo primero.</summary>
    [Fact]
    public void Sin_credencial_un_403_es_falta_de_autenticacion()
        => Of("HTTP 403", hasToken: false).Should().Be(AgentProblem.NotAuthenticated);

    /// <summary>Se reconoce anidado, que es como el SDK lo envuelve.</summary>
    [Fact]
    public void La_cuota_se_reconoce_dentro_de_otra_excepcion()
        => CopilotFailure.Classify(
                new InvalidOperationException("session.send", new Exception(RealQuotaError)), true)
            .Should().Be(AgentProblem.QuotaExhausted);

    /// <summary>
    /// El orden es la corrección: un error que menciona las dos cosas se resuelve por la más
    /// específica. Sin este orden, «quota» seguiría cayendo en el cajón del asiento.
    /// </summary>
    [Fact]
    public void La_cuota_gana_al_asiento_cuando_el_texto_menciona_las_dos()
        => Of("Your subscription has exceeded its monthly quota")
            .Should().Be(AgentProblem.QuotaExhausted);

    /// <summary>Y las excepciones de transporte se reconocen por su tipo, no por su texto.</summary>
    [Fact]
    public void Un_fallo_de_transporte_es_red_aunque_su_texto_no_lo_diga()
        => CopilotFailure.Classify(new HttpRequestException("boom"), hasToken: true)
            .Should().Be(AgentProblem.Offline);

    // ============================================================ la excepción que viaja

    [Fact]
    public void La_excepcion_del_proveedor_lleva_problema_y_crudo_hasta_la_vista()
    {
        var ex = new AuditorProviderException(
            CopilotHelp.QuotaExhausted("mensual"), AgentProblem.QuotaExhausted, "crudo del SDK");

        ex.Problem.Should().Be(AgentProblem.QuotaExhausted);
        ex.Detail.Should().Be("crudo del SDK");
        ex.IsRetryable.Should().BeFalse();
    }

    /// <summary>
    /// La de autenticación sigue siendo una de ellas: los <c>catch</c> que ya la nombraban no
    /// cambian de sentido, y el resto deja de colarse por debajo.
    /// </summary>
    [Fact]
    public void La_de_autenticacion_sigue_siendo_una_excepcion_de_proveedor()
        => new AuditorAuthenticationException(CopilotHelp.NoAccount)
            .Should().BeAssignableTo<AuditorProviderException>();

    [Fact]
    public void Y_la_de_modelo_tambien_lleva_su_problema()
        => new AuditorModelUnavailableException("gpt-5").Problem
            .Should().Be(AgentProblem.ModelUnavailable);
}
