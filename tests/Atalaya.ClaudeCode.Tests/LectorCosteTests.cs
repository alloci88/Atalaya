using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Atalaya.Agents;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// F30 §2e — <b>LO QUE CUESTA CONSUMIR EL FLUJO</b>, que es una salvaguarda y no una curiosidad.
/// <para>
/// <b>Por qué es una regla y no una métrica.</b> El bucle que lee la salida del CLI es el mismo que
/// tiene que consumir las cuentas de F21 y el <c>result</c> que cierra el turno, y está leyendo la
/// tubería de un proceso vivo: <b>un proceso cuya tubería de salida se llena se bloquea
/// escribiendo</b> hasta que alguien lea. Así que todo lo que ese bucle haga en línea se lo cobra
/// al CLI en tiempo de pared. Ya pasó una vez —§2d: un <c>Dispatcher.Invoke</c> síncrono por
/// token— y la forma de que no vuelva a pasar sin avisar es ponerle número.
/// </para>
/// <para>
/// <b>De dónde salen los topes.</b> De la configuración (a) de la medida de §2e: la pasada 1 de
/// <c>CalculadoraCarga.cs</c> con el CLI 2.1.263 duró <b>54,0 s</b>. Lo que el lector gaste tiene
/// que ser despreciable contra eso, y los topes de aquí están puestos donde lo son —bajo el 3 %—
/// con holgura de un orden de magnitud sobre lo medido, para que un banco cargado no los roce.
/// </para>
/// </summary>
public sealed class LectorCosteTests
{
    /// <summary>
    /// <b>El flujo REAL del CLI, reproducido 200 veces</b>: 2.400 eventos con todas las formas que
    /// el CLI manda de verdad —<c>init</c>, <c>status</c>, <c>rate_limit_event</c>, los bloques de
    /// contenido, las cuentas y el <c>result</c>—. Medido: ~10 ms. El tope está en 250, que sigue
    /// siendo el 0,5 % de la pasada de (a).
    /// <para>
    /// Lo que salta aquí es cualquier cosa cara <b>por evento</b>: una escritura a disco, una ida
    /// al hilo de interfaz, un registro que abra un fichero. Ninguna de las tres cabe en 100 µs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Consumir_el_turno_real_doscientas_veces_no_le_cuesta_nada_a_la_pasada()
    {
        string stream = string.Concat(Enumerable.Repeat(Recording(), 200));

        // Una vez en frío para que el JIT no se cuente como coste del lector.
        await new ClaudeStreamReader().ReadAsync(new StringReader(stream), CancellationToken.None);

        var clock = Stopwatch.StartNew();
        await new ClaudeStreamReader().ReadAsync(new StringReader(stream), CancellationToken.None);
        clock.Stop();

        clock.Elapsed.TotalMilliseconds.Should().BeLessThan(250,
            "el bucle que lee la tubería del CLI no puede hacer nada caro en línea: mientras no "
            + "lee, el CLI se bloquea escribiendo (F30 §2e)");
    }

    /// <summary>
    /// <b>Y contar los argumentos crece con lo escrito, no con su cuadrado.</b> Es la regla que la
    /// primera versión rompía: guardaba el JSON acumulado y le pasaba una expresión regular entera
    /// <b>en cada trozo</b>. Medido con esta misma forma —200 hallazgos, 183 KB de argumentos en
    /// trozos del tamaño de un token, 45.867 eventos—: <b>5,5 s</b> entonces, <b>0,15 s</b> ahora.
    /// Cinco segundos y medio dentro del bucle de lectura son cinco segundos y medio con el CLI
    /// parado.
    /// <para>
    /// El tope de 1,5 s deja pasar lo lineal con diez veces de margen y no deja pasar lo
    /// cuadrático. Se comprobó con un cebo: devolviendo <c>ToolCallInput</c> a la versión de
    /// regex, este test se pone rojo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Contar_los_argumentos_de_una_llamada_crece_con_lo_escrito_y_no_con_su_cuadrado()
    {
        string stream = ToolCallStream(findings: 200);

        var clock = Stopwatch.StartNew();
        var narrated = new List<ToolStream>();
        await new ClaudeStreamReader(onTool: narrated.Add)
            .ReadAsync(new StringReader(stream), CancellationToken.None);
        clock.Stop();

        narrated.Count(t => t.Phase == ToolStreamPhase.Input).Should().Be(200,
            "los doscientos elementos tienen que haberse contado: un test de coste sobre un lector "
            + "que no cuenta nada no mide nada");

        clock.Elapsed.TotalMilliseconds.Should().BeLessThan(1_500,
            "contar lo que el modelo escribe no puede costar el cuadrado de lo escrito");
    }

    /// <summary>
    /// Una llamada a <c>submit_findings</c> con la forma de una real: los argumentos escritos en
    /// trozos del tamaño de un token, que es como llegan (<c>input_json_delta</c>).
    /// </summary>
    private static string ToolCallStream(int findings)
    {
        var args = new StringBuilder("{\"findings\":[");
        for (int i = 0; i < findings; i++)
        {
            args.Append(i == 0 ? string.Empty : ",")
                .Append("{\"title\":\"Hallazgo ").Append(i + 1).Append(" de la unidad\",")
                .Append("\"severity\":\"media\",\"path\":\"src/Servicios/CalculadoraCarga.cs\",")
                .Append("\"evidence\":\"").Append(new string('x', 700)).Append("\"}");
        }

        args.Append("]}");

        var stream = new StringBuilder()
            .AppendLine("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":2,"output_tokens":1}}}}""")
            .AppendLine("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"mcp__atalaya__submit_findings"}}}""");

        string json = args.ToString();
        for (int i = 0; i < json.Length; i += 4)
        {
            stream.Append("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":""")
                .Append(JsonSerializer.Serialize(json.Substring(i, Math.Min(4, json.Length - i))))
                .AppendLine("}}}");
        }

        return stream
            .AppendLine("""{"type":"stream_event","event":{"type":"content_block_stop","index":0}}""")
            .ToString();
    }

    /// <summary>La misma grabación que reproduce <see cref="RealStreamReplayTests"/>.</summary>
    private static string Recording()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(
            dir!.FullName, "tests", "Atalaya.ClaudeCode.Tests", "Grabaciones", "turno-real.jsonl"));
    }
}
