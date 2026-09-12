using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// A2 — Los documentos de gobierno se sostienen solos, y nadie compila un documento.
/// <para>
/// <c>DECISIONS.md</c> se había convertido en la única memoria del repositorio: el historial
/// completo, las normas de la casa y el estado de lo que hay construido, todo en el mismo sitio y
/// creciendo. A2 separa las tres cosas — <c>docs/ESTADO.md</c> dice qué hay hoy, <c>docs/NORMAS.md</c>
/// dice cómo se trabaja, y DECISIONS se queda con lo que siempre fue: el registro de por qué.
/// </para>
/// <para>
/// El precio de esa separación es que ahora hay tres documentos que pueden contradecirse en
/// silencio, y una contradicción entre documentos no rompe ninguna compilación ni pone ningún test
/// en rojo — se descubre el día que alguien sigue una referencia y no llega a ningún sitio, o lee
/// una norma que otra copia ya había cambiado. Estos tres tests son lo único que lo vigila.
/// </para>
/// </summary>
public sealed class DocsTests
{
    private const string Decisiones = "DECISIONS.md";
    private const string Estado = "docs/ESTADO.md";
    private const string Normas = "docs/NORMAS.md";

    /// <summary>La última norma de la casa: NORMAS las tiene todas, de la N-1 a esta.</summary>
    private const int UltimaNorma = 10;

    // Los topes de ESTADO, con nombre: son la frontera entre un resumen y un segundo DECISIONS.
    private const int TopeDeLineas = 1_500;
    private const int TopeDeBytes = 120 * 1024;

    /// <summary>Un bloque de decisión: <c>### D-nnn …</c> o <c>- **D-nnn …</c>.</summary>
    private static readonly Regex BloqueDeDecision =
        new(@"^(?:### |- \*\*)(D-\d+)\b", RegexOptions.Compiled);

    /// <summary>Una cita a una decisión, suelta en mitad del texto.</summary>
    private static readonly Regex CitaDeDecision = new(@"\bD-\d+\b", RegexOptions.Compiled);

    // ================================================================ las citas llevan a algún sitio

    /// <summary>
    /// Toda decisión que ESTADO cita existe como bloque en DECISIONS.
    /// <para>
    /// Lo que se rompería en silencio: una referencia a una decisión que nadie escribió —o que se
    /// escribió con otro número—. El lector la sigue, busca el bloque, y no hay bloque. ESTADO
    /// resume sin justificar, que es exactamente lo contrario de para lo que se hizo; y como un
    /// <c>D-</c> mal tecleado se lee igual de bien que uno bueno, no lo nota nadie hasta que ya
    /// hay varios.
    /// </para>
    /// </summary>
    [Fact]
    public void Toda_decision_citada_en_el_estado_existe_como_bloque_en_las_decisiones()
    {
        var escritas = new HashSet<string>(StringComparer.Ordinal);
        foreach (string linea in File.ReadLines(Doc(Decisiones)))
        {
            Match bloque = BloqueDeDecision.Match(linea);
            if (bloque.Success)
            {
                escritas.Add(bloque.Groups[1].Value);
            }
        }

        escritas.Should().NotBeEmpty("si aquí no sale ninguna, lo roto es la forma de leer DECISIONS");

        string[] citadas = CitaDeDecision.Matches(Texto(Estado))
            .Select(cita => cita.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        string[] huerfanas = citadas.Where(id => !escritas.Contains(id)).ToArray();

        huerfanas.Should().BeEmpty(
            "docs/ESTADO.md cita {0} decisiones y estas no existen como bloque en DECISIONS.md: {1}",
            citadas.Length, string.Join(", ", huerfanas));
    }

    // ================================================================ el tope de ESTADO

    /// <summary>
    /// ESTADO cabe de una sentada: <c>1.500</c> líneas y <c>120 kB</c>.
    /// <para>
    /// Lo que se rompería en silencio: que ESTADO vuelva a ser DECISIONS. Nadie añade mil líneas de
    /// golpe — se añaden veinte cada vez, cada una justificada, y un día el documento que existía
    /// para poder leerse entero ya no se lee entero. Entonces se vuelve a resumir en otro sitio y
    /// A2 no ha servido de nada. El tope es lo que convierte «esto también es importante» en una
    /// decisión consciente de qué se quita a cambio.
    /// </para>
    /// </summary>
    [Fact]
    public void El_estado_se_mantiene_dentro_de_su_tope_para_poder_leerse_entero()
    {
        var fichero = new FileInfo(Doc(Estado));
        int lineas = File.ReadLines(fichero.FullName).Count();
        long bytes = fichero.Length;

        string medido = $"docs/ESTADO.md mide {lineas} líneas y {bytes} bytes";

        lineas.Should().BeLessThanOrEqualTo(TopeDeLineas,
            "{0}, y el tope es de {1} líneas: lo que no cabe se resume o se queda en DECISIONS",
            medido, TopeDeLineas);

        bytes.Should().BeLessThanOrEqualTo(TopeDeBytes,
            "{0}, y el tope es de {1} bytes: lo que no cabe se resume o se queda en DECISIONS",
            medido, TopeDeBytes);
    }

    // ================================================================ una norma, una copia

    /// <summary>
    /// Las diez normas están enteras en NORMAS, una sola vez cada una, y su texto ya no vive en
    /// DECISIONS.
    /// <para>
    /// Lo que se rompería en silencio: dos copias de la misma norma que divergen. Alguien matiza
    /// la N-5 donde la encuentra primero, la otra copia se queda como estaba, y a partir de ese día
    /// hay dos versiones de cómo se trabaja aquí sin que nadie sepa cuál manda. Por eso se busca el
    /// <b>encabezado</b> (<c>**N-n — </c>) y no el número: DECISIONS sigue citando las normas por su
    /// número suelto —<c>N-5</c>, <c>(N-2)</c>, <c>N-6/N-7</c>— por todas partes, y eso es legítimo
    /// y tiene que seguir siéndolo. Lo que no puede quedar ahí es el texto de la norma.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_norma_de_la_casa_se_escribe_una_sola_vez_y_solo_en_las_normas()
    {
        string normas = Texto(Normas);
        string decisiones = Texto(Decisiones);

        for (int n = 1; n <= UltimaNorma; n++)
        {
            string encabezado = $"**N-{n} — ";

            int enNormas = Apariciones(normas, encabezado);
            enNormas.Should().Be(1,
                "el encabezado «{0}» aparece {1} vez/veces en docs/NORMAS.md: la norma se escribe "
                + "ahí, entera y una sola vez", encabezado, enNormas);

            int enDecisiones = Apariciones(decisiones, encabezado);
            enDecisiones.Should().Be(0,
                "el encabezado «{0}» aparece {1} vez/veces en DECISIONS.md: el texto de la norma "
                + "vive en docs/NORMAS.md y solo ahí — citarla por su número sí, copiarla no",
                encabezado, enDecisiones);
        }
    }

    private static int Apariciones(string texto, string aguja)
    {
        int total = 0;
        for (int i = texto.IndexOf(aguja, StringComparison.Ordinal);
             i >= 0;
             i = texto.IndexOf(aguja, i + aguja.Length, StringComparison.Ordinal))
        {
            total++;
        }

        return total;
    }

    private static DirectoryInfo Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!;
    }

    private static string Doc(string relative)
        => Path.Combine(Root().FullName, relative.Replace('/', Path.DirectorySeparatorChar));

    // El BOM de los .md se lo come el lector: lo que llega es el texto, sin un carácter invisible
    // delante que rompería el primer encabezado del fichero.
    private static string Texto(string relative) => File.ReadAllText(Doc(relative));
}
