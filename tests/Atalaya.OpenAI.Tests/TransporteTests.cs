using System.Net;

namespace Atalaya.OpenAI.Tests;

/// <summary>
/// <b>El transporte de PROV-3</b>: la petición, el SSE, la taxonomía de errores y el reintento
/// acotado (comportamientos 1, 5, 7 y 8 de la spec).
/// <para>
/// Todo corre contra <see cref="EndpointFalso"/>. <b>Ningún test toca la red</b> y ninguno usa una
/// clave de verdad: las que aparecen aquí son texto inventado, y están justamente para comprobar
/// que no se escapan a ningún sitio donde se puedan leer.
/// </para>
/// </summary>
public sealed class TransporteTests
{
    private const string ClaveDeMentira = "sk-esto-no-es-una-clave";

    private static readonly IReadOnlyList<ChatMessage> UnMensaje = new[] { ChatMessage.User("hola") };

    private static readonly IReadOnlyList<ChatToolSpec> SinHerramientas = Array.Empty<ChatToolSpec>();

    // ==================================================================================== (5) el SSE

    /// <summary>
    /// <b>Regla: los argumentos de una llamada llegan PARTIDOS y se concatenan, casando los deltas
    /// por <c>index</c></b> (§5).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Todo lo demás. Un lector que se quede con el último
    /// delta —que es lo que sale de tratar el trozo como un objeto y no como un delta— devuelve la
    /// llamada con el <c>id</c> y el nombre correctos y los argumentos a MEDIAS. El bucle la
    /// ejecutaría con medio JSON: el toolbox rechazaría la forma, el modelo vería un error que no
    /// entiende y la unidad acabaría sin hallazgos. Nada de eso enseña «el transporte perdió la
    /// segunda mitad».
    /// </para>
    /// </summary>
    [Fact]
    public async Task Los_argumentos_partidos_se_ensamblan_en_una_sola_llamada()
    {
        const string argumentos =
            """{"findingId":"01J0","ruta":"src/Atalaya.App/Services/CostEstimator.cs","linea":184}""";

        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeHerramienta("report_finding", argumentos));

        using OpenAiClient cliente = Cliente(falso);
        ChatTurn turno = await cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        turno.ToolCalls.Should().ContainSingle(
            "los tres deltas son UNA llamada, no tres: se casan por su índice");
        turno.ToolCalls[0].Id.Should().Be("call_1");
        turno.ToolCalls[0].Name.Should().Be("report_finding");
        turno.ToolCalls[0].ArgumentsJson.Should().Be(
            argumentos,
            "los argumentos vienen a trozos y se concatenan; quedarse con el último delta deja "
            + "medio JSON");
        turno.FinishReason.Should().Be("tool_calls");
    }

    /// <summary>
    /// <b>Regla: un <c>usage</c> que no llega es null, jamás cero</b> (§5, y el XML de
    /// <see cref="ChatUsage"/>).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Hay endpoints de este dialecto que no mandan consumo al
    /// hacer streaming, y varios mandan <c>"usage": null</c> en cada trozo. Un lector que inicialice
    /// el consumo a cero —o que lea ese null como un objeto vacío— produce una sesión que declara
    /// haber gastado 0 tokens. Eso no falla nada: se suma al periodo, sale un coste de 0,00 $ y el
    /// agregado se da por completo cuando en realidad falta un proveedor entero. Null es lo que
    /// hace que el pie del coste pueda decir que no lo sabe (D-787).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_usage_que_no_llega_es_null_y_nunca_cero()
    {
        var falso = new EndpointFalso();
        falso.RespondeStream(
            """{"choices":[{"index":0,"delta":{"content":"sin"}}],"usage":null}""",
            """{"choices":[{"index":0,"delta":{"content":" consumo"}}],"usage":null}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

        using OpenAiClient cliente = Cliente(falso);
        ChatTurn turno = await cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        turno.Text.Should().Be("sin consumo");
        turno.Usage.Should().BeNull(
            "«no lo dijo» y «gastó cero» son cosas distintas, y confundirlas mete un cero falso "
            + "en el agregado de costes");
    }

    /// <summary>
    /// <b>Regla: cuando el consumo SÍ llega se lee entero, con
    /// <c>prompt_tokens_details.cached_tokens</c> si está y 0 si no</b> (§5).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Los cacheados no se cobran igual que los frescos, y este
    /// proveedor declara <c>InputIncludesCache</c>: si el transporte no los lee, la fórmula
    /// descuenta cero y el coste sale inflado — un número creíble y equivocado, que es el peor de
    /// los dos. Y al revés, un endpoint que no manda el desglose no puede acabar con cacheados
    /// inventados.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_consumo_se_lee_entero_y_los_cacheados_solo_si_el_endpoint_los_da()
    {
        var conCache = new EndpointFalso();
        conCache.RespondeStream(EndpointFalso.TurnoDeTexto("ok"));

        using (OpenAiClient cliente = Cliente(conCache))
        {
            ChatTurn turno = await cliente.SendAsync(UnMensaje, SinHerramientas, null, default);
            turno.Usage.Should().Be(new ChatUsage(120, 8, 100));
        }

        var sinCache = new EndpointFalso();
        sinCache.RespondeStream(EndpointFalso.TurnoDeHerramienta("report_finding", "{}"));

        using (OpenAiClient cliente = Cliente(sinCache))
        {
            ChatTurn turno = await cliente.SendAsync(UnMensaje, SinHerramientas, null, default);
            turno.Usage.Should().Be(
                new ChatUsage(200, 30, 0),
                "sin `prompt_tokens_details` los cacheados son 0, que aquí sí es la verdad");
        }
    }

    /// <summary>
    /// <b>Regla: el texto se entrega a <c>onText</c> SEGÚN llega, no de una pieza al final</b>
    /// (§5, F30).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Un lector que acumule y llame una sola vez al terminar
    /// devuelve exactamente el mismo <see cref="ChatTurn"/>: no falla ni una aserción sobre el
    /// texto. Lo único que cambia es que el hilo de actividad se queda en blanco durante toda la
    /// unidad y luego escupe el párrafo entero de golpe — y eso no lo nota ningún test que solo
    /// mire el resultado.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_texto_se_entrega_segun_llega_y_no_de_una_pieza_al_final()
    {
        const string dicho = "Revisando CostEstimator";

        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeTexto(dicho));

        var trozos = new List<string>();
        using OpenAiClient cliente = Cliente(falso);
        ChatTurn turno = await cliente.SendAsync(UnMensaje, SinHerramientas, trozos.Add, default);

        turno.Text.Should().Be(dicho);
        string.Concat(trozos).Should().Be(dicho);
        trozos.Count.Should().Be(
            dicho.Length,
            "el hilo de actividad se alimenta delta a delta; juntarlos y entregarlos al final deja "
            + "la pantalla muda toda la unidad");
    }

    /// <summary>
    /// <b>Regla: un 200 que no es streaming se para, no se devuelve como un turno vacío</b> (§5).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Es el fallo de la familia de D-782, y el peor de todos.
    /// Una URL base que no es la de una API —un portal de proxy, una página de login, la raíz de un
    /// sitio— contesta 200 con HTML o JSON normal. El lector de SSE no encuentra un solo
    /// <c>data:</c>, devuelve un turno sin texto y sin llamadas, y la unidad termina «con éxito» y
    /// sin un hallazgo. Nadie ve un error; se ve una auditoría que no encontró nada.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_200_que_no_es_streaming_se_para_en_vez_de_devolver_un_turno_vacio()
    {
        var falso = new EndpointFalso();
        falso.RespondeJson("""{"id":"x","choices":[{"message":{"content":"hola"}}]}""");

        using OpenAiClient cliente = Cliente(falso);
        Func<Task> llamada = () => cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        (await llamada.Should().ThrowAsync<AuditorProviderException>())
            .Which.Message.Should().Contain(
                "streaming",
                "un turno vacío sin error es una unidad que termina bien y sin hallazgos");
    }

    // ============================================================================ (1) y (8) la petición

    /// <summary>
    /// <b>Regla: se pide <c>POST {baseUrl}/chat/completions</c> con <c>stream</c>, las
    /// herramientas y <c>tool_choice</c> automático</b> (§1).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Cada pieza que falte se cae de una manera distinta y
    /// ninguna dice su nombre: sin <c>stream</c> el endpoint contesta un JSON normal y el turno sale
    /// vacío; sin <c>tools</c> el modelo no tiene forma de reportar nada y la unidad acaba en cero
    /// hallazgos; sin <c>tool_choice</c> hay endpoints que nunca llaman a una herramienta y se
    /// limitan a describir lo que haría; y sin <c>stream_options.include_usage</c> los endpoints
    /// que sí saben mandar consumo no lo mandan y el coste sale en blanco.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_peticion_pide_streaming_con_las_herramientas_y_eleccion_automatica()
    {
        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeTexto("ok"));

        var herramientas = new[]
        {
            new ChatToolSpec(
                "report_finding",
                "Reporta un hallazgo.",
                """{"type":"object","properties":{"ruta":{"type":"string"}}}"""),
        };

        using OpenAiClient cliente = Cliente(falso);
        await cliente.SendAsync(UnMensaje, herramientas, null, default);

        PeticionVista vista = falso.Recibidas.Single();
        vista.Metodo.Should().Be("POST");
        vista.Url.Should().Be("https://api.ejemplo.com/v1/chat/completions");
        vista.Cuerpo.Should().Contain("\"stream\":true");
        vista.Cuerpo.Should().Contain("\"include_usage\":true");
        vista.Cuerpo.Should().Contain("\"tool_choice\":\"auto\"");
        vista.Cuerpo.Should().Contain("report_finding");
        vista.Cuerpo.Should().Contain("\"model\":\"mi-modelo\"");
    }

    /// <summary>
    /// <b>Regla: la clave viaja como la pida el endpoint —<c>Bearer</c> o <c>api-key</c>— y en un
    /// endpoint local sin clave no se manda ninguna cabecera</b> (§1).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> No en silencio del todo, pero sí de la peor manera: con
    /// el modo equivocado una clave perfectamente buena devuelve 401, y quien lo ve se pone a
    /// generar claves nuevas en vez de tocar el desplegable — es el único motivo de que
    /// <see cref="OpenAiAuth"/> sea un enum. Y mandar una cabecera de autorización vacía a Ollama o
    /// LM Studio cierra la única puerta que no pasa por una factura.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(OpenAiAuth.Bearer, "Authorization", "Bearer " + ClaveDeMentira)]
    [InlineData(OpenAiAuth.ApiKey, "api-key", ClaveDeMentira)]
    public async Task La_clave_viaja_como_la_pide_el_endpoint(
        OpenAiAuth modo, string cabecera, string valor)
    {
        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeTexto("ok"));

        using OpenAiClient cliente = Cliente(falso, auth: modo);
        await cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        falso.Recibidas.Single().Cabeceras.Should().ContainKey(cabecera);
        falso.Recibidas.Single().Cabeceras[cabecera].Should().Be(valor);
    }

    [Fact]
    public async Task Un_endpoint_local_sin_clave_se_llama_igual_y_sin_cabecera()
    {
        var falso = new EndpointFalso();
        falso.RespondeStream(EndpointFalso.TurnoDeTexto("ok"));

        using OpenAiClient cliente = Cliente(falso, url: "http://localhost:11434/v1", clave: null);
        await cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        falso.Llamadas.Should().Be(1);
        falso.Recibidas[0].Cabeceras.Should().NotContainKey("Authorization");
        falso.Recibidas[0].Cabeceras.Should().NotContainKey("api-key");
    }

    /// <summary>
    /// <b>Regla: un <c>http://</c> que no es local no llega a llamarse</b> — la decide
    /// <see cref="OpenAiEndpoint.WhyNot"/>, que es donde está escrita (§8).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Literalmente en silencio: la petición saldría, el
    /// endpoint contestaría, la auditoría funcionaría — y el prompt con el código auditado dentro y
    /// la clave de API habrían viajado en claro por la red de la empresa. No falla nada, y por eso
    /// hay que comprobar que el transporte USA la regla en vez de limitarse a tenerla escrita en
    /// otro fichero.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_endpoint_en_claro_que_no_es_local_no_llega_a_llamarse()
    {
        var falso = new EndpointFalso();

        using OpenAiClient cliente = Cliente(falso, url: "http://una-maquina-de-la-red/v1");
        Func<Task> llamada = () => cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        await llamada.Should().ThrowAsync<AuditorAuthenticationException>();
        falso.Llamadas.Should().Be(0, "no se manda nada en claro ni para enterarse de si contesta");
    }

    // ================================================================================ (7) la taxonomía

    /// <summary>
    /// <b>Regla: cada código HTTP sale con su causa del vocabulario común y con su tipo de
    /// excepción</b> (§7).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> El circuito de errores de la aplicación decide QUÉ
    /// ofrecer mirando el <c>Problem</c>, no el texto. Un 429 clasificado como credencial manda a
    /// alguien a repegar una clave que está bien; un 401 clasificado como red hace que Atalaya
    /// reintente contra una API de pago; un 404 sin su tipo propio pierde el enlace a Ajustes que
    /// arregla el caso de dos clics. Ninguna de esas tres cosas rompe una compilación ni un test
    /// que solo mire que «lanza».
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(401, AgentProblem.NotAuthenticated)]
    [InlineData(403, AgentProblem.NotAuthenticated)]
    [InlineData(404, AgentProblem.ModelUnavailable)]
    [InlineData(429, AgentProblem.QuotaExhausted)]
    [InlineData(503, AgentProblem.Offline)]
    [InlineData(400, AgentProblem.Unknown)]
    public async Task Cada_codigo_sale_con_su_causa_del_vocabulario_comun(int codigo, AgentProblem causa)
    {
        var falso = new EndpointFalso();
        falso.RespondeError((HttpStatusCode)codigo, """{"error":{"message":"lo que sea"}}""");
        falso.RespondeError((HttpStatusCode)codigo, """{"error":{"message":"lo que sea"}}""");

        using OpenAiClient cliente = Cliente(falso);
        Func<Task> llamada = () => cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        AuditorProviderException error =
            (await llamada.Should().ThrowAsync<AuditorProviderException>()).Which;

        error.Problem.Should().Be(causa);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.Detail.Should().Contain(
            codigo.ToString(),
            "el crudo se copia y se pega para buscar la petición en el panel del endpoint");
    }

    /// <summary>
    /// <b>Regla: el 401/403 y el 404 llevan su hija propia</b>, que es lo que hace que la interfaz
    /// pueda ofrecer el gesto exacto sin volver a mirar un código HTTP (§7).
    /// </summary>
    [Fact]
    public async Task La_credencial_y_el_modelo_traen_su_excepcion_propia()
    {
        var credencial = new EndpointFalso();
        credencial.RespondeError(HttpStatusCode.Unauthorized);

        using (OpenAiClient cliente = Cliente(credencial))
        {
            Func<Task> llamada = () => cliente.SendAsync(UnMensaje, SinHerramientas, null, default);
            await llamada.Should().ThrowAsync<AuditorAuthenticationException>();
        }

        var modelo = new EndpointFalso();
        modelo.RespondeError(HttpStatusCode.NotFound);

        using (OpenAiClient cliente = Cliente(modelo))
        {
            Func<Task> llamada = () => cliente.SendAsync(UnMensaje, SinHerramientas, null, default);
            AuditorModelUnavailableException error =
                (await llamada.Should().ThrowAsync<AuditorModelUnavailableException>()).Which;

            error.ModelId.Should().Be("mi-modelo");
            error.Message.Should().Contain("URL base", "un 404 es el modelo O la dirección, y lo dice");
        }
    }

    /// <summary>
    /// <b>Regla: ningún mensaje de esta casa nombra a otra</b> (§7).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> El barrido de desacople exime a
    /// <c>src/Atalaya.OpenAI/</c> entero —es la casa que puede nombrarse a sí misma—, así que un
    /// «reinstala el CLI» copiado de otro clasificador entra aquí sin que nadie se entere y manda a
    /// quien está configurando una URL y una clave a instalar un programa que no tiene nada que ver.
    /// Los nombres van partidos por lo mismo que en el barrido: un test que lleve escrita entera la
    /// cadena que busca se encuentra a sí mismo.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_mensaje_de_esta_casa_nombra_a_otra()
    {
        string[] otras = { "Cop" + "ilot", "Claude" + " Code", "Git" + "Hub", "Anthro" + "pic" };

        var frases = new List<string>
        {
            OpenAiFailure.NoKey,
            OpenAiFailure.NoModel,
            OpenAiFailure.Quota,
            OpenAiFailure.NotStreaming,
            OpenAiFailure.Credential(401),
            OpenAiFailure.ModelOrUrl("mi-modelo"),
            OpenAiFailure.NotResponding(503),
            OpenAiFailure.Unreachable(string.Empty),
            OpenAiFailure.Unknown(400),
        };

        var culpables = new List<string>();
        foreach (string frase in frases)
        {
            foreach (string otra in otras)
            {
                if (frase.Contains(otra, StringComparison.OrdinalIgnoreCase))
                {
                    culpables.Add($"«{otra}» en: {frase}");
                }
            }
        }

        culpables.Should().BeEmpty(
            "quien lee esto está configurando una URL y una clave; mandarle a mirar otra casa le "
            + "hace perder la tarde:" + Environment.NewLine + string.Join(Environment.NewLine, culpables));
    }

    // ============================================================================= (7) el reintento

    /// <summary>
    /// <b>Regla: se reintenta UNA vez, y solo en 429 y 5xx</b> (§7).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Sin el reintento, un 503 de dos segundos tira una unidad
    /// entera de auditoría que ya se había pagado — y el usuario ve «el proveedor no responde» de
    /// un endpoint que va perfectamente. No es un fallo visible: es trabajo perdido que se achaca a
    /// la red.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Se_reintenta_una_vez_en_429_y_en_5xx(int codigo)
    {
        var falso = new EndpointFalso();
        falso.RespondeError((HttpStatusCode)codigo);
        falso.RespondeStream(EndpointFalso.TurnoDeTexto("ok"));

        using OpenAiClient cliente = Cliente(falso);
        ChatTurn turno = await cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        turno.Text.Should().Be("ok");
        falso.Llamadas.Should().Be(2, "uno, y solo uno: un segundo reintento no es un backoff");
    }

    /// <summary>
    /// <b>Regla: un 4xx no se reintenta NUNCA</b> (§7).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Lo contrario de silencio para el bolsillo y muy silencioso
    /// en los tests: una clave que no vale no empieza a valer por mandarla otra vez, y cada reintento
    /// contra una API de pago es una línea en la factura. Un bucle que reintente «por si acaso» pasa
    /// todos los tests de la taxonomía —la excepción sigue siendo la correcta— y solo se nota al mes
    /// siguiente. El endpoint falso lo caza porque solo tiene UNA respuesta grabada: si hubiera un
    /// segundo intento, reventaría con otra excepción distinta.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(400)]
    public async Task Un_4xx_no_se_reintenta_nunca(int codigo)
    {
        var falso = new EndpointFalso();
        falso.RespondeError((HttpStatusCode)codigo);

        using OpenAiClient cliente = Cliente(falso);
        Func<Task> llamada = () => cliente.SendAsync(UnMensaje, SinHerramientas, null, default);

        await llamada.Should().ThrowAsync<AuditorProviderException>();
        falso.Llamadas.Should().Be(1, "reintentar un 4xx contra una API de pago es una factura");
    }

    /// <summary>
    /// <b>Regla: parar la sesión no es un fallo del proveedor</b> (§7).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Un <c>catch</c> que envuelva todo lo que se cancela
    /// convierte el botón de parar en un aviso rojo de «el endpoint no responde» — y, peor, lo hace
    /// reintentable: el usuario para, Atalaya reintenta, y la sesión que se quería cortar sigue
    /// gastando. El tope de D-208 y una cancelación del usuario llegan por la MISMA excepción, y
    /// distinguirlas es todo lo que separa las dos cosas.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Parar_la_sesion_no_se_convierte_en_un_error_del_proveedor()
    {
        var falso = new EndpointFalso();
        using var parado = new CancellationTokenSource();
        parado.Cancel();

        using OpenAiClient cliente = Cliente(falso);
        Func<Task> llamada = () => cliente.SendAsync(UnMensaje, SinHerramientas, null, parado.Token);

        await llamada.Should().ThrowAsync<OperationCanceledException>();
        falso.Llamadas.Should().Be(0, "y desde luego no se reintenta lo que el usuario acaba de parar");
    }

    // ================================================================================ «Probar» (§2)

    /// <summary>
    /// <b>Regla: «Probar» usa la llamada más barata que demuestre lo que hace falta</b> — la lista
    /// de modelos, que no gasta un token, y solo baja a un turno mínimo si el endpoint no la sirve
    /// o no lista el modelo.
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Un «Probar» que siempre mande un turno completo funciona
    /// igual de bien y cobra por cada clic en un botón que la gente pulsa tres veces seguidas
    /// mientras configura.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Probar_no_gasta_un_token_si_el_modelo_esta_en_la_lista()
    {
        var falso = new EndpointFalso();
        falso.RespondeJson("""{"data":[{"id":"otro"},{"id":"mi-modelo"}]}""");

        using OpenAiClient cliente = Cliente(falso);
        ChatProbe probe = await cliente.ProbeAsync(default);

        probe.Ok.Should().BeTrue();
        probe.Message.Should().Contain("mi-modelo");
        falso.Llamadas.Should().Be(1);
        falso.Recibidas[0].Url.Should().EndWith("/v1/models");
        falso.Recibidas[0].Metodo.Should().Be("GET");
    }

    /// <summary>
    /// <b>Regla: «Probar» devuelve una frase para una persona, y ni ella ni el crudo llevan la
    /// clave</b> (el XML de <see cref="ChatProbe"/>).
    /// <para>
    /// <b>Qué se rompería en silencio.</b> Dos cosas a la vez. Una, que un <c>ProbeAsync</c> que
    /// deje escapar la excepción tumba la pantalla de Ajustes en vez de escribir una línea debajo
    /// del botón. Y dos, que el detalle copiable se enseña para pegarlo en un correo o en un tique:
    /// una clave ahí dentro sale de la máquina en cuanto alguien lo copia, y no hay forma de
    /// retirarla.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Probar_traduce_el_fallo_a_una_frase_y_no_deja_escapar_la_clave()
    {
        var falso = new EndpointFalso();
        falso.RespondeError(HttpStatusCode.NotFound);                      // `/models` no la sirve
        falso.RespondeError(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");

        using OpenAiClient cliente = Cliente(falso);
        ChatProbe probe = await cliente.ProbeAsync(default);

        probe.Ok.Should().BeFalse();
        probe.Message.Should().Contain("Ajustes", "una frase dice qué revisar y DÓNDE");
        probe.Message.Should().NotContain(ClaveDeMentira);
        (probe.Detail ?? string.Empty).Should().NotContain(
            ClaveDeMentira,
            "el crudo se copia y se pega; lo que lleve dentro sale de esta máquina");
        falso.Llamadas.Should().Be(2);
    }

    // ==================================================================================== andamiaje

    private static OpenAiClient Cliente(
        EndpointFalso falso,
        string url = "https://api.ejemplo.com/v1",
        string modelo = "mi-modelo",
        string? clave = ClaveDeMentira,
        OpenAiAuth auth = OpenAiAuth.Bearer)
        => new(() => new OpenAiEndpoint(url, modelo, auth), () => clave, falso)
        {
            // Los tests no duermen: lo que se prueba es que hay UN reintento, no cuánto se espera.
            RetryWait = TimeSpan.Zero,
        };
}
