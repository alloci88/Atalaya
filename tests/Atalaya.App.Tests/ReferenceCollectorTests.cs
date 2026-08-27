using System.Text;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F6.8 — <b>el prompt de arreglo viaja con sus referencias</b>.
/// <para>
/// El defecto que abrió el parte: a un agente se le enseñaba SOLO el método afectado, aplicaba un
/// arreglo localmente correcto —una validación, una excepción nueva— y rompía un proceso aguas
/// arriba, porque nadie le había dicho quién llamaba a ese método ni qué esperaban los llamadores.
/// El caso real de esta misma aplicación auditada: añadir una excepción a un método que antes
/// truncaba en silencio rompe a cualquier llamador que dependiera del truncado.
/// </para>
/// <para>
/// Lo que estos tests fijan es lo comprobable de eso: que la lista de llamadores es REAL (no la
/// declaración, no los comentarios, no las cadenas), que los topes recortan sin mentir, que cuando
/// no se puede mirar se dice en vez de dejar entender que no hay llamadores, y que las reglas del
/// contrato llegan al prompt.
/// </para>
/// </summary>
public sealed class ReferenceCollectorTests : IDisposable
{
    private readonly List<string> _temps = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ReferenceCollector _collector = new();

    public void Dispose()
    {
        foreach (string dir in _temps)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
                // Un temporal que no se deja borrar no invalida el test.
            }
        }
    }

    // ---------------------------------------------------------------- el fixture

    /// <summary>
    /// El método afectado. Dentro hay dos menciones a su propio nombre que NO son llamadas: la
    /// <c>cref</c> de la documentación y el comentario de dentro del cuerpo. Ninguna puede aparecer
    /// como llamador — es justo lo que separa un análisis de código de un <c>grep</c>.
    /// </summary>
    private const string Afectado = """
        namespace Common;

        public static class Hex
        {
            /// <summary>Ver <see cref="HexStringToByteArray"/> para el formato.</summary>
            public static byte[] HexStringToByteArray(string hex)
            {
                // HexStringToByteArray trunca en silencio si la longitud es impar.
                int len = hex.Length / 2;
                return new byte[len];
            }
        }
        """;

    /// <summary>
    /// Dos llamadas de verdad, en dos miembros distintos, más una cadena literal con el mismo
    /// nombre dentro que tampoco es una llamada.
    /// </summary>
    private const string Llamadores = """
        namespace Loader;

        public sealed class DetLoader
        {
            public byte[] Cargar(string hex)
            {
                return Common.Hex.HexStringToByteArray(hex);
            }

            public string Nombre() => "HexStringToByteArray";

            public int Longitud(string hex)
            {
                byte[] raw = Common.Hex.HexStringToByteArray(hex);
                return raw.Length;
            }
        }
        """;

    /// <summary>Un clon con el método afectado, sus dos llamadores y una carpeta de compilación.</summary>
    private string NewClone()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-refs-" + Guid.NewGuid().ToString("N"));
        _temps.Add(root);

        Write(root, "src/Common/Common.csproj", "<Project />");
        Write(root, "src/Common/Hex.cs", Afectado);
        Write(root, "src/Loader/Loader.csproj", "<Project />");
        Write(root, "src/Loader/Loader.cs", Llamadores);

        // Lo compilado no es código fuente: una llamada aquí dentro no es un llamador.
        Write(root, "src/Loader/bin/Debug/Loader.g.cs", Llamadores);
        return root;
    }

    private static void Write(string root, string relative, string content)
    {
        string abs = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    /// <summary>La línea de <c>HexStringToByteArray</c> dentro de <see cref="Afectado"/>.</summary>
    private const int LineaDelHallazgo = 6;

    private Finding Seed(
        string path = "src/Common/Hex.cs",
        string? symbol = "Hex.HexStringToByteArray",
        int line = LineaDelHallazgo,
        string title = "Truncado silencioso al convertir hexadecimal")
    {
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-2), AuditMode.Lotes, "0123456789abcdef", "alvaro");

        return new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = "BUG-0003",
            RuleId = "errores.calculo.negocio",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = title,
            Description = "Trunca sin avisar cuando la longitud es impar.",
            Impact = "Se pierden bytes del identificador.",
            Recommendation = "Validar la longitud antes de convertir.",
            Locations = { new Location(path, line) },
            Symbol = symbol,
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 1,
        };
    }

    // =========================================================== §1 la lista es REAL

    /// <summary>
    /// El caso central: un método usado en varios sitios devuelve esos sitios, con su ruta, su
    /// línea, el miembro que los contiene y la línea de la llamada.
    /// </summary>
    [Fact]
    public void Un_metodo_usado_en_varios_sitios_devuelve_sus_llamadores_con_ruta_linea_y_miembro()
    {
        ReferenceReport refs = _collector.Collect(NewClone(), Seed());

        refs.Collected.Should().BeTrue();
        refs.Precision.Should().Be(ReferencePrecision.Sintaxis);
        refs.Symbols.Should().Contain("HexStringToByteArray");
        refs.Total.Should().Be(2, "hay dos llamadas de verdad, y nada más es una llamada");

        refs.Sites.Should().OnlyContain(s => s.Path == "src/Loader/Loader.cs");
        refs.Sites.Select(s => s.Member).Should().Equal("DetLoader.Cargar", "DetLoader.Longitud");
        refs.Sites.Should().OnlyContain(s => s.Text.Contains("Hex.HexStringToByteArray(hex)"));
        refs.Sites.Select(s => s.Line).Should().OnlyContain(l => l > 0);
    }

    /// <summary>
    /// La DECLARACIÓN del método no es un uso suyo. Si contara, todo hallazgo diría «usado desde 1
    /// sitio» y ese sitio sería él mismo — un dato que no informa de nada y que además haría creer
    /// que el método tiene consumidores cuando no los tiene.
    /// </summary>
    [Fact]
    public void La_declaracion_del_propio_metodo_no_cuenta_como_llamador()
        => _collector.Collect(NewClone(), Seed()).Sites
            .Should().NotContain(s => s.Path == "src/Common/Hex.cs");

    /// <summary>
    /// Ni la documentación, ni los comentarios, ni las cadenas. Es la diferencia entre analizar el
    /// código y buscar el nombre: el fichero afectado menciona su propio nombre dos veces más y el
    /// de los llamadores una tercera, y ninguna de las tres es una llamada.
    /// </summary>
    [Fact]
    public void Ni_la_documentacion_ni_los_comentarios_ni_las_cadenas_son_llamadas()
    {
        ReferenceReport refs = _collector.Collect(NewClone(), Seed());

        refs.Total.Should().Be(2);
        refs.Sites.Should().NotContain(s => s.Text.StartsWith("//", StringComparison.Ordinal));
        refs.Sites.Should().NotContain(s => s.Text.Contains("public string Nombre()"));
    }

    /// <summary>Lo compilado no es fuente: <c>bin</c> y compañía no se miran.</summary>
    [Fact]
    public void Las_carpetas_de_compilacion_no_aportan_llamadores()
        => _collector.Collect(NewClone(), Seed()).Sites
            .Should().NotContain(s => s.Path.Contains("/bin/", StringComparison.Ordinal));

    /// <summary>
    /// Sin el <c>symbol</c> declarado, el miembro se deduce de la UBICACIÓN leyendo el clon: es lo
    /// que tienen los hallazgos anteriores a que <c>Finding.Symbol</c> existiera (D-223).
    /// </summary>
    [Fact]
    public void Sin_symbol_declarado_el_miembro_sale_de_la_ubicacion()
    {
        ReferenceReport refs = _collector.Collect(NewClone(), Seed(symbol: null));

        refs.Symbols.Should().Contain("HexStringToByteArray");
        refs.Total.Should().Be(2);
    }

    /// <summary>
    /// <b>El símbolo declarado MANDA sobre la línea guardada.</b> Salió del X-BLAST real: BUG-0002
    /// declara <c>StringToByteArray</c>, pero su línea ya no cae dentro de ese método porque el
    /// fichero se ha editado desde la auditoría, y la ubicación aportaba <c>ReadCSV</c>. Sumando
    /// las dos fuentes la lista traía nueve llamadores de los que ocho eran de otro método. Una
    /// lista diluida es peor que una corta.
    /// </summary>
    [Fact]
    public void El_symbol_declarado_manda_sobre_una_linea_que_se_ha_quedado_vieja()
    {
        // La línea 10 cae dentro del método, pero apuntando a otro miembro cualquiera el símbolo
        // declarado tiene que seguir ganando: aquí se apunta al final del fichero, fuera de él.
        ReferenceReport refs = _collector.Collect(NewClone(), Seed(line: 12));

        refs.Symbols.Should().Equal("HexStringToByteArray");
        refs.Total.Should().Be(2);
    }

    /// <summary>
    /// Un hallazgo puede nombrar VARIOS miembros hermanos («DateToByteArray,TimeToByteArray»), y
    /// de cada entrada se toma el miembro, nunca el tipo: buscar la clase devolvería cada línea
    /// que la menciona y ahogaría a los llamadores del método.
    /// </summary>
    [Theory]
    [InlineData("Hex.HexStringToByteArray", "HexStringToByteArray")]
    [InlineData("HexStringToByteArray/Otro", "HexStringToByteArray|Otro")]
    [InlineData("Common.Hex.HexStringToByteArray, Common.Hex.Otro", "HexStringToByteArray|Otro")]
    public void El_campo_symbol_puede_nombrar_varios_miembros(string symbol, string expected)
        => _collector.TargetSymbols(NewClone(), Seed(symbol: symbol))
            .Should().Equal(expected.Split('|'));

    // =========================================================== §1 sin llamadores

    /// <summary>
    /// «No se encontraron llamadores» es un RESULTADO, no un fallo — y se distingue de «no se pudo
    /// mirar», que es lo que separa un método muerto de una API pública consumida desde fuera.
    /// </summary>
    [Fact]
    public void Un_metodo_sin_llamadores_lo_dice_y_no_es_un_fallo()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-refs-" + Guid.NewGuid().ToString("N"));
        _temps.Add(root);
        Write(root, "src/Common/Hex.cs", Afectado);

        ReferenceReport refs = _collector.Collect(root, Seed());

        refs.Collected.Should().BeTrue("mirar y no encontrar nada es haber mirado");
        refs.Total.Should().Be(0);
        refs.Sites.Should().BeEmpty();
        refs.Unavailable.Should().BeNull();
    }

    /// <summary>
    /// Y sin clon no se puede mirar: la diferencia con lo anterior es toda la que hay entre «no lo
    /// usa nadie» y «no lo sé», y es la que autoriza o no a cambiar el contrato.
    /// </summary>
    [Fact]
    public void Sin_clon_local_no_se_finge_una_respuesta()
    {
        ReferenceReport refs = _collector.Collect(clonePath: null, Seed());

        refs.Collected.Should().BeFalse();
        refs.Unavailable.Should().Contain("clon");
        refs.Total.Should().Be(0);
    }

    // =========================================================== §3 los topes

    /// <summary>
    /// El tope de sitios: se listan los primeros y se dice cuántos quedan y EN QUÉ PROYECTOS. Una
    /// lista recortada en silencio se lee como la lista entera.
    /// </summary>
    [Fact]
    public void Por_encima_del_tope_se_listan_los_primeros_y_se_dice_cuantos_quedan()
    {
        string root = NewClone();
        Write(root, "src/Extra/Extra.csproj", "<Project />");
        Write(root, "src/Extra/Bulk.cs", Bulk(40));

        ReferenceReport refs = _collector.Collect(root, Seed());

        refs.Total.Should().Be(42, "40 del bulto y 2 del llamador de siempre");
        refs.Sites.Should().HaveCount(ReferenceBudget.Default.MaxSites);
        refs.Hidden.Should().Be(12);
        refs.OverflowAreas.Should().Equal("Extra", "Loader");
    }

    /// <summary>
    /// El presupuesto de tiempo corta y lo DICE. Generar el prompt nunca puede tardar minutos, y
    /// una lista recortada por el reloj que no se anunciara sería una lista falsamente completa.
    /// </summary>
    [Fact]
    public void Agotado_el_presupuesto_de_tiempo_se_corta_y_se_avisa()
    {
        var sinTiempo = new ReferenceBudget(TimeSpan.Zero, 30, 9000);

        ReferenceReport refs = _collector.Collect(NewClone(), Seed(), sinTiempo);

        refs.TimedOut.Should().BeTrue();
        refs.Collected.Should().BeTrue("cortar por tiempo no es no haber podido mirar");
    }

    /// <summary>El tope de tamaño es el cinturón del de sitios: treinta líneas larguísimas también sobran.</summary>
    [Fact]
    public void El_tope_de_tamano_recorta_aunque_quepan_los_sitios()
    {
        string root = NewClone();
        Write(root, "src/Extra/Bulk.cs", Bulk(20));

        ReferenceReport refs = _collector.Collect(root, Seed(), new ReferenceBudget(TimeSpan.FromSeconds(20), 30, 400));

        refs.Total.Should().Be(22);
        refs.Sites.Count.Should().BeLessThan(22, "el presupuesto de caracteres manda antes que el de sitios");
        refs.Sites.Should().NotBeEmpty("un tope minúsculo nunca deja la sección vacía");
    }

    private static string Bulk(int calls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("namespace Extra;");
        sb.AppendLine();
        sb.AppendLine("public sealed class Bulto");
        sb.AppendLine("{");
        for (int i = 0; i < calls; i++)
        {
            sb.AppendLine($"    public byte[] Uso{i}(string hex) => Common.Hex.HexStringToByteArray(hex);");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    // =========================================================== §1 el plan B textual

    /// <summary>
    /// Un stack que no es C# no tiene analizador: se busca el nombre como palabra completa y el
    /// informe queda ETIQUETADO como aproximado. Media lista encontrada vale más que ninguna,
    /// siempre que no se presente como precisa.
    /// </summary>
    [Fact]
    public void Fuera_de_C_sostenido_se_cae_a_la_busqueda_de_texto_ETIQUETADA()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-refs-" + Guid.NewGuid().ToString("N"));
        _temps.Add(root);
        Write(root, "app/util.py", "def parse_hex(s):\n    return bytes.fromhex(s)\n");
        Write(root, "app/main.py", "from util import parse_hex\n\nraw = parse_hex(dato)\nx = parse_hexadecimal(dato)\n");

        ReferenceReport refs = _collector.Collect(root, Seed("app/util.py", "parse_hex", line: 1));

        refs.Precision.Should().Be(ReferencePrecision.Texto);
        refs.Sites.Should().Contain(s => s.Path == "app/main.py" && s.Text.Contains("raw = parse_hex(dato)"));
        refs.Sites.Should().NotContain(
            s => s.Text.Contains("parse_hexadecimal"),
            "el nombre se busca como PALABRA: parse_hex no está dentro de parse_hexadecimal");
    }

    /// <summary>La palabra completa, no el trozo. Es lo único que hace usable el plan B.</summary>
    [Theory]
    [InlineData("var x = Parse(v);", "Parse", true)]
    [InlineData("var x = Parser(v);", "Parse", false)]
    [InlineData("var x = ReParse(v);", "Parse", false)]
    [InlineData("Parse", "Parse", true)]
    [InlineData("obj.Parse();", "Parse", true)]
    public void La_busqueda_textual_casa_palabras_no_trozos(string line, string word, bool expected)
        => ReferenceCollector.ContainsWord(line, word).Should().Be(expected);

    // =========================================================== §2 el prompt

    /// <summary>
    /// El prompt de un método con llamadores: la sección con la lista real, y el aviso de que son
    /// DIRECTOS con la frase del impacto de segundo orden que NO se calcula.
    /// </summary>
    [Fact]
    public void El_prompt_de_un_metodo_con_llamadores_trae_la_lista_y_el_alcance_de_lo_que_dice()
    {
        Finding f = Seed();
        string prompt = FixPromptBuilder.Build(f, _collector.Collect(NewClone(), f));

        prompt.Should().Contain("## Quién usa este código");
        prompt.Should().Contain("`HexStringToByteArray`");
        prompt.Should().Contain("src/Loader/Loader.cs:");
        prompt.Should().Contain("DetLoader.Cargar");
        prompt.Should().Contain("Hex.HexStringToByteArray(hex)");
        prompt.Should().Contain("DIRECTOS");
        prompt.Should().Contain("segundo orden");
        prompt.Should().NotContain("No se encontraron llamadores");
    }

    /// <summary>Sin llamadores, el prompt lo dice — y advierte de la API pública que no se ve desde aquí.</summary>
    [Fact]
    public void El_prompt_de_un_metodo_sin_llamadores_lo_dice_y_avisa_de_la_API_publica()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-refs-" + Guid.NewGuid().ToString("N"));
        _temps.Add(root);
        Write(root, "src/Common/Hex.cs", Afectado);

        Finding f = Seed();
        string prompt = FixPromptBuilder.Build(f, _collector.Collect(root, f));

        prompt.Should().Contain("No se encontraron llamadores en esta solución");
        prompt.Should().Contain("API pública consumida");
        prompt.Should().Contain("compatibilidad");
    }

    /// <summary>La lista aproximada llega ETIQUETADA como tal, con sus dos clases de error.</summary>
    [Fact]
    public void El_prompt_etiqueta_la_lista_obtenida_por_busqueda_de_texto()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-refs-" + Guid.NewGuid().ToString("N"));
        _temps.Add(root);
        Write(root, "app/util.py", "def parse_hex(s):\n    return s\n");
        Write(root, "app/main.py", "raw = parse_hex(dato)\n");

        Finding f = Seed("app/util.py", "parse_hex", line: 1);
        string prompt = FixPromptBuilder.Build(f, _collector.Collect(root, f));

        prompt.Should().Contain("búsqueda de texto");
        prompt.Should().Contain("falsos positivos");
        prompt.Should().Contain("falsos negativos");
    }

    /// <summary>
    /// Y cuando no se pudo mirar, el prompt sale IGUAL —no generarlo sería peor— pero diciendo que
    /// va sin referencias y por qué. La frase que no puede aparecer nunca aquí es la de «no tiene
    /// llamadores»: es exactamente el permiso para cambiar el contrato a ciegas.
    /// </summary>
    [Fact]
    public void Sin_recoleccion_el_prompt_sale_igual_pero_avisa_de_que_va_sin_referencias()
    {
        Finding f = Seed();
        string prompt = FixPromptBuilder.Build(f, _collector.Collect(clonePath: null, f));

        prompt.Should().Contain("SIN la lista de llamadores");
        prompt.Should().Contain("clon");
        prompt.Should().Contain("No supongas que el código no se usa");
        prompt.Should().NotContain("No se encontraron llamadores");
        prompt.Should().Contain("## Reglas del arreglo", "las reglas no dependen de las referencias");
    }

    /// <summary>Y el prompt de siempre, sin informe ninguno, sigue componiéndose.</summary>
    [Fact]
    public void El_prompt_sin_informe_de_referencias_avisa_igual()
        => FixPromptBuilder.Build(Seed()).Should().Contain("SIN la lista de llamadores");

    /// <summary>El recorte se dice también en el prompt, con el número y los proyectos.</summary>
    [Fact]
    public void El_prompt_dice_cuantos_sitios_no_lista_y_donde_estan()
    {
        string root = NewClone();
        Write(root, "src/Extra/Extra.csproj", "<Project />");
        Write(root, "src/Extra/Bulk.cs", Bulk(40));

        Finding f = Seed();
        string prompt = FixPromptBuilder.Build(f, _collector.Collect(root, f));

        prompt.Should().Contain("…y 12 más en Extra, Loader");
        prompt.Should().Contain("de 42 en total");
    }

    // =========================================================== §2 las reglas

    /// <summary>
    /// Las reglas endurecidas: el contrato observable, adaptar a los llamadores cuando el contrato
    /// cambia, y compilar y pasar los tests. Sin ellas la lista sería decorativa.
    /// </summary>
    [Fact]
    public void Las_reglas_del_arreglo_giran_alrededor_del_contrato_observable()
    {
        string prompt = FixPromptBuilder.Build(Seed());

        prompt.Should().Contain("## Reglas del arreglo");
        prompt.Should().Contain("Preserva el contrato observable");
        prompt.Should().Contain("revisa TODOS los llamadores listados");
        prompt.Should().Contain("truncaba en silencio", "el ejemplo real es la regla hecha concreta");
        prompt.Should().Contain("adapta cada llamador afectado en el mismo cambio");
        prompt.Should().Contain("Un arreglo que rompe llamadores no es un arreglo");
        prompt.Should().Contain("Compila la solución y ejecuta los tests");
    }

    /// <summary>Y los criterios de siempre no se pierden por el camino (§5.7).</summary>
    [Fact]
    public void Los_criterios_de_aceptacion_de_siempre_siguen_en_el_prompt()
    {
        string prompt = FixPromptBuilder.Build(Seed());

        prompt.Should().Contain("Arregla SOLO este hallazgo");
        prompt.Should().Contain("Lista al final los ficheros tocados");
    }

    /// <summary>
    /// El límite del AGENTE, no el de la app: revisar los llamadores listados y no explorar más
    /// allá. Un arreglo que se expande por la solución no es un arreglo mejor — es uno que ya no
    /// se puede revisar.
    /// </summary>
    [Fact]
    public void El_prompt_le_pone_limites_al_agente_que_arregla()
    {
        string prompt = FixPromptBuilder.Build(Seed());

        prompt.Should().Contain("### Límites de la exploración");
        prompt.Should().Contain("NO explores el código base más allá");
        prompt.Should().Contain("NO lo persigas");
        prompt.Should().Contain("riesgo pendiente de revisión humana");
        prompt.Should().Contain("mínimo y quirúrgico");
    }
}
