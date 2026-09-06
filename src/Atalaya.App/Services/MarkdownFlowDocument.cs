using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace Atalaya.App.Services;

/// <summary>
/// Convierte el markdown de un informe en un <see cref="FlowDocument"/> (F6.3 §2).
/// <para>
/// <b>Por qué esto y no una librería de render.</b> Ver DECISIONS (F6.3). En corto: analizar
/// markdown a mano sería un error —es un formato con esquinas— y por eso el ANÁLISIS lo hace
/// Markdig, que es el analizador de referencia y está vivo. Lo que no se delega es el DIBUJO:
/// <c>Markdig.Wpf</c> trae sus propios estilos fijos, que no siguen el tema de la aplicación, y
/// las tablas —que estos informes usan a cada página— son justo su punto débil. Recorriendo el
/// árbol de Markdig se dibuja con la <see cref="WpfTable"/> nativa de WPF, que hace reparto real
/// de columnas, y con los pinceles del tema por <c>DynamicResource</c>, así que claro y oscuro
/// salen bien sin una segunda paleta que mantener.
/// </para>
/// <para>
/// Los enlaces NO navegan dentro de la aplicación: se los pasa a quien llama, que los abre en el
/// navegador. Un informe es un documento, no un sitio web dentro de la ventana.
/// </para>
/// </summary>
public static class MarkdownFlowDocument
{
    /// <summary>
    /// EL CUERPO DE UN INFORME ES EL CUERPO DEL SISTEMA (F26 Parte C, D-962): 15, no 13,5. Un
    /// informe es lo más largo que se lee en Atalaya y estaba escrito en el tamaño más pequeño de
    /// la aplicación anterior a F26.
    /// <para>
    /// El número es el de respaldo: con una <c>Application</c> viva, el tamaño sale del token
    /// <c>FontSize.Body</c> por referencia de recurso, así que la escala se cambia en un sitio y
    /// el informe la sigue. Sin aplicación —los tests— vale este.
    /// </para>
    /// </summary>
    private const double BaseFontSize = 15;

    /// <summary>Los tres de la escala que el informe usa, con su interlineado (D-944.3, UI-0059).</summary>
    private const double SmallFontSize = 14;

    /// <inheritdoc cref="SmallFontSize"/>
    private const double MetaFontSize = 13;

    /// <inheritdoc cref="SmallFontSize"/>
    private const double BaseLineHeight = 21;

    /// <inheritdoc cref="SmallFontSize"/>
    private const double MetaLineHeight = 18;

    /// <summary>
    /// Los seis niveles de encabezado, de H1 a H6, y sus tokens.
    /// <para>
    /// No arrancan en <c>FontSize.H1</c>: el título del informe ya lo enseña la cabecera del visor
    /// y repetirlo a 30 px dentro del documento haría de la primera pantalla una portada. El H1 del
    /// markdown entra por H2 y la escala baja desde ahí hasta el cuerpo.
    /// </para>
    /// </summary>
    private static readonly (string Key, double Fallback)[] HeadingSizes =
    {
        ("FontSize.H2", 26),
        ("FontSize.H3", 21),
        ("FontSize.Lead", 17),
        ("FontSize.Body", 15),
        ("FontSize.Body", 15),
        ("FontSize.Body", 15),
    };

    /// <summary>
    /// LA MEDIDA DE LECTURA (F26 Parte C): el texto se para aquí aunque la ventana siga. Una línea
    /// de 1.600 px no se puede seguir con la vista, y un informe es texto largo. El anexo técnico
    /// —que es quien trae las tablas de nueve columnas— se pinta aparte y sin este tope.
    /// </summary>
    private const double ReadMaxWidth = 720;

    /// <summary>
    /// Las cuatro gravedades tal y como el informe las escribe: entre corchetes en el encabezado de
    /// un hallazgo (<c>#### [Crítica] …</c>) y contadas en la línea de resumen (<c>3 Críticas ·
    /// 27 Altas</c>). Son los DOS sitios donde el informe habla de gravedad, y los dos se pintan
    /// con la pastilla del sistema en vez de con texto entre corchetes.
    /// </summary>
    /// <summary>
    /// <b>«Critica» sin tilde también cuenta</b> (R10 §4, F29). El escritor del informe ya pone la
    /// tilde, pero los informes que se escribieron antes NO se reescriben —son el registro de lo
    /// que pasó— y los suyos dicen «Critica». Un resolutor que solo entiende la grafía nueva deja
    /// sin pastilla justo la gravedad más alta de todo lo ya archivado.
    /// </summary>
    private static readonly Regex SeverityMarks = new(
        @"\[(?<b>Crítica|Critica|Alta|Media|Baja)\]"
        + @"|(?<n>\d+)\s+(?<w>Críticas|Crítica|Criticas|Critica|Altas|Alta|Medias|Media|Bajas|Baja)\b",
        RegexOptions.Compiled);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        // Tablas de tubería: es lo que escriben nuestros informes. Nada de extensiones de más:
        // lo que el generador no emite, el visor no tiene por qué saber pintarlo.
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    /// <summary>
    /// El documento listo para un <c>FlowDocumentScrollViewer</c>.
    /// </summary>
    /// <param name="openLink">
    /// Qué hacer con un enlace del informe. Se llama con la URL tal cual; null lo deja inerte.
    /// </param>
    /// <param name="measure">
    /// La medida de lectura, en píxeles. <see cref="ReadMaxWidth"/> por defecto; <c>0</c> la quita,
    /// que es lo que necesita el anexo técnico —sus tablas son de nueve columnas y en 720 px se
    /// parten—.
    /// </param>
    public static FlowDocument Build(
        string? markdown, Action<string>? openLink = null, double measure = ReadMaxWidth)
    {
        var doc = new FlowDocument
        {
            FontSize = BaseFontSize,
            LineHeight = BaseLineHeight,
            PagePadding = new Thickness(0, 0, 14, 24),

            // EN BANDERA, COMO TODO LO DEMÁS (UI-0031). No había un solo `TextAlignment="Justify"`
            // en el árbol: el valor por DEFECTO de `FlowDocument` en WPF es justificado, y nadie
            // lo había puesto ni quitado. En una medida de ~700 px, sin partición de palabras
            // —WPF no la tiene—, justificar abre ríos de tres espacios entre palabras. Era el
            // único texto de la aplicación que no iba en bandera.
            TextAlignment = TextAlignment.Left,
            // Un ancho de columna infinito: el visor tiene su propio scroll y el informe se lee
            // en una sola columna, no en dos como haría el reparto por defecto.
            ColumnWidth = double.PositiveInfinity,
            Background = Brushes.Transparent,
        };

        // La MEDIDA DE LECTURA. `MaxPageWidth` para el texto donde deja de poder seguirse; el
        // visor sigue ocupando el ancho que tenga, así que la columna queda a la izquierda de su
        // panel y no flotando en medio de una tarjeta.
        if (measure > 0)
        {
            doc.MaxPageWidth = measure;
        }

        // LA TIPOGRAFÍA SALE DEL SISTEMA (UI-0059). El tamaño base ya se pedía por clave; la
        // FAMILIA y el INTERLINEADO se escribían a mano —`new FontFamily("Segoe UI")` y
        // `BaseFontSize * 1.55` = 23,25 px cuando `LineHeight.Body` es 21, que es el 1,4 de
        // D-944.3—. Es la vista de la que D-993 dijo que se leería «con la escala del sistema».
        Size(doc, "FontSize.Body", BaseFontSize);
        Family(doc, "Font.Ui", "Segoe UI Variable Text, Segoe UI");
        Line(doc, "LineHeight.Body", BaseLineHeight);

        Theme(doc, TextElement.ForegroundProperty, "TextFillColorPrimaryBrush", Brushes.Black);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return doc;
        }

        MarkdownDocument parsed = Markdown.Parse(markdown, Pipeline);
        foreach (MdBlock block in parsed)
        {
            if (Convert(block, openLink) is { } rendered)
            {
                doc.Blocks.Add(rendered);
            }
        }

        return doc;
    }

    // ---------- Bloques ----------

    private static WpfBlock? Convert(MdBlock block, Action<string>? openLink) => block switch
    {
        HeadingBlock heading => Heading(heading, openLink),
        ParagraphBlock paragraph => Paragraph(paragraph, openLink),
        ListBlock list => List(list, openLink),
        QuoteBlock quote => Quote(quote, openLink),
        CodeBlock code => Code(code),
        MdTable table => Table(table, openLink),
        ThematicBreakBlock => Rule(),
        // HTML crudo dentro de un informe: se ignora en vez de escupirlo como texto. Nuestros
        // generadores no lo emiten, y pintar «<div>» sería peor que no pintar nada.
        _ => null,
    };

    private static WpfBlock Heading(HeadingBlock heading, Action<string>? openLink)
    {
        int level = Math.Clamp(heading.Level, 1, HeadingSizes.Length);
        (string key, double fallback) = HeadingSizes[level - 1];
        var paragraph = new Paragraph
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level == 1 ? 0 : 18, 0, 6),
        };

        Size(paragraph, key, fallback);

        // EL ENCABEZADO DE UN HALLAZGO abre con su gravedad entre corchetes: `#### [Alta] …`. Se
        // resuelve AQUÍ y no en el reconocimiento de texto normal porque Markdig ve `[Alta]` como
        // una referencia de enlace sin destino y la parte en tres trozos —«[», «Alta», «]»—, así
        // que ningún literal contiene nunca la marca entera. Con el texto plano del encabezado
        // delante sí se ve, y es el único sitio del informe donde este caso aparece.
        if (Application.Current is not null && HeadingSeverity(heading) is var (severity, rest))
        {
            paragraph.Inlines.Add(Pill(severity, severity));
            paragraph.Inlines.Add(new Run(" " + rest));
            return Ruled(paragraph, level);
        }

        Fill(paragraph.Inlines, heading.Inline, openLink);
        return Ruled(paragraph, level);
    }

    /// <summary>
    /// Una raya bajo H1 y H2 separa las secciones largas sin necesidad de más espacio en blanco,
    /// que es lo que un informe de tres pantallas agradece.
    /// </summary>
    private static WpfBlock Ruled(Paragraph paragraph, int level)
    {
        if (level <= 2)
        {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 5);
            Theme(paragraph, WpfBlock.BorderBrushProperty,
                "CardStrokeColorDefaultBrush", Brushes.LightGray);
        }

        return paragraph;
    }

    /// <summary>
    /// La gravedad con la que abre el encabezado de un hallazgo, y lo que va detrás. Null cuando el
    /// encabezado no empieza por una — que es el caso de todos los demás.
    /// </summary>
    internal static (string Severity, string Title)? HeadingSeverity(HeadingBlock heading)
    {
        string text = PlainText(heading.Inline).TrimStart();
        // Las dos grafías de «Crítica», por lo mismo que en `SeverityMarks`: un informe ya escrito
        // no se reescribe. Lo que se DEVUELVE es siempre la buena: se acepta la vieja para no
        // dejar sin pastilla lo ya archivado, no para volver a pintarla sin tilde al lado de las
        // demás.
        Match m = Regex.Match(text, @"^\[(Crítica|Critica|Alta|Media|Baja)\]\s*");
        return m.Success
            ? (m.Groups[1].Value == "Critica" ? "Crítica" : m.Groups[1].Value, text[m.Length..])
            : null;
    }

    /// <summary>El texto de un encabezado sin su formato. Solo se usa para leerlo, no para pintarlo.</summary>
    private static string PlainText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (MdInline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline nested:
                    sb.Append(PlainText(nested));
                    break;
            }
        }

        return sb.ToString();
    }

    private static WpfBlock Paragraph(ParagraphBlock block, Action<string>? openLink)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        Fill(paragraph.Inlines, block.Inline, openLink);
        return paragraph;
    }

    /// <summary>
    /// Las listas van como <see cref="WpfList"/> nativa: la sangría, las
    /// viñetas y la numeración —incluido el número de arranque, que nuestros informes no usan pero
    /// un importado sí puede— las hace WPF, y anidan solas.
    /// </summary>
    private static WpfBlock List(ListBlock block, Action<string>? openLink)
    {
        var list = new WpfList
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(22, 0, 0, 0),
            MarkerStyle = block.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        };

        if (block.IsOrdered && int.TryParse(block.OrderedStart, out int start) && start > 0)
        {
            list.StartIndex = start;
        }

        foreach (MdBlock child in block)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var listItem = new ListItem { Margin = new Thickness(0, 0, 0, 3) };
            foreach (MdBlock inner in item)
            {
                if (Convert(inner, openLink) is { } rendered)
                {
                    // Dentro de una viñeta, el párrafo no lleva su hueco de abajo: con él, una
                    // lista de diez elementos se lee como diez párrafos sueltos.
                    if (rendered is Paragraph p)
                    {
                        p.Margin = new Thickness(0);
                    }

                    listItem.Blocks.Add(rendered);
                }
            }

            if (listItem.Blocks.Count > 0)
            {
                list.ListItems.Add(listItem);
            }
        }

        return list;
    }

    private static WpfBlock Quote(QuoteBlock block, Action<string>? openLink)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12, 2, 0, 2),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };

        Theme(section, WpfBlock.BorderBrushProperty,
            "AccentTextFillColorPrimaryBrush", Brushes.SteelBlue);
        Theme(section, TextElement.ForegroundProperty, "TextFillColorSecondaryBrush", Brushes.DimGray);

        foreach (MdBlock child in block)
        {
            if (Convert(child, openLink) is { } rendered)
            {
                section.Blocks.Add(rendered);
            }
        }

        return section;
    }

    /// <summary>Un bloque de código: monoespaciado, con fondo propio y sin ajuste de línea.</summary>
    private static WpfBlock Code(CodeBlock block)
    {
        // 13 y no 13,5 (UI-0059): `BaseFontSize - 1.5` no era ninguno de los tres tamaños de la
        // escala, y su interlineado propio tampoco. Los dos salen de los tokens.
        var paragraph = new Paragraph(new Run(TextOf(block)))
        {
            FontSize = MetaFontSize,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(12, 9, 12, 9),
            BorderThickness = new Thickness(1),
            LineHeight = MetaLineHeight,
        };

        Size(paragraph, "FontSize.Meta", MetaFontSize);
        Family(paragraph, "Font.Mono", "Cascadia Code, Consolas, Courier New");
        Line(paragraph, "LineHeight.Meta", MetaLineHeight);

        Theme(paragraph, WpfBlock.BackgroundProperty,
            "ControlFillColorDefaultBrush", Brushes.WhiteSmoke);
        Theme(paragraph, WpfBlock.BorderBrushProperty,
            "CardStrokeColorDefaultBrush", Brushes.LightGray);

        return paragraph;
    }

    private static string TextOf(CodeBlock block)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < block.Lines.Count; i++)
        {
            sb.Append(block.Lines.Lines[i].Slice.ToString());
            if (i < block.Lines.Count - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static WpfBlock Rule()
    {
        var rule = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 14),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        Theme(rule, WpfBlock.BorderBrushProperty,
            "CardStrokeColorDefaultBrush", Brushes.LightGray);
        return rule;
    }

    /// <summary>
    /// La tabla, que es la razón de haber escrito este renderizador. Una columna por columna
    /// declarada, cabecera con fondo y peso propios, y rejilla de una línea: lo que hace que un
    /// desglose por unidad se pueda leer en diagonal.
    /// </summary>
    private static WpfBlock Table(MdTable table, Action<string>? openLink)
    {
        var wpf = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 14),
        };

        var rows = table.OfType<MdTableRow>().ToList();
        int columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (int i = 0; i < columns; i++)
        {
            wpf.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        wpf.RowGroups.Add(group);

        foreach (MdTableRow row in rows)
        {
            var wpfRow = new WpfTableRow();
            if (row.IsHeader)
            {
                wpfRow.FontWeight = FontWeights.SemiBold;
                Theme(wpfRow, TextElement.BackgroundProperty,
                    "SubtleFillColorSecondaryBrush", Brushes.WhiteSmoke);
            }

            for (int i = 0; i < row.Count; i++)
            {
                var cell = new WpfTableCell
                {
                    Padding = new Thickness(9, 5, 9, 5),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    TextAlignment = AlignmentOf(table, i),
                };

                Theme(cell, WpfTableCell.BorderBrushProperty, "CardStrokeColorDefaultBrush", Brushes.LightGray);

                if (row[i] is MdTableCell source)
                {
                    foreach (MdBlock child in source)
                    {
                        if (Convert(child, openLink) is { } rendered)
                        {
                            if (rendered is Paragraph p)
                            {
                                p.Margin = new Thickness(0);
                            }

                            cell.Blocks.Add(rendered);
                        }
                    }
                }

                wpfRow.Cells.Add(cell);
            }

            group.Rows.Add(wpfRow);
        }

        return wpf;
    }

    /// <summary>La alineación declarada por la fila de guiones (<c>---:</c>), si la declara.</summary>
    private static TextAlignment AlignmentOf(MdTable table, int column)
    {
        if (column >= table.ColumnDefinitions.Count)
        {
            return TextAlignment.Left;
        }

        return table.ColumnDefinitions[column].Alignment switch
        {
            TableColumnAlign.Center => TextAlignment.Center,
            TableColumnAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

    // ---------- Inlines ----------

    private static void Fill(InlineCollection target, ContainerInline? container, Action<string>? openLink)
    {
        if (container is null)
        {
            return;
        }

        foreach (MdInline inline in container)
        {
            foreach (WpfInline rendered in Convert(inline, openLink))
            {
                target.Add(rendered);
            }
        }
    }

    private static IEnumerable<WpfInline> Convert(MdInline inline, Action<string>? openLink)
    {
        switch (inline)
        {
            case LiteralInline literal:
                foreach (WpfInline piece in Severities(literal.Content.ToString()))
                {
                    yield return piece;
                }

                break;

            case CodeInline code:
                yield return InlineCode(code.Content);
                break;

            case EmphasisInline emphasis:
                yield return Emphasis(emphasis, openLink);
                break;

            case LinkInline { IsImage: false } link:
                yield return Link(link, openLink);
                break;

            case LinkInline image:
                // Una imagen en un informe de auditoría no existe hoy; si llegara una, se escribe
                // su texto alternativo en vez de dejar un hueco mudo.
                yield return new Run(image.Title ?? image.Url ?? "(imagen)");
                break;

            case AutolinkInline auto:
                yield return Link(auto.Url, auto.Url, openLink);
                break;

            case LineBreakInline lineBreak:
                yield return lineBreak.IsHard ? new LineBreak() : new Run(" ");
                break;

            case ContainerInline container:
                var span = new Span();
                Fill(span.Inlines, container, openLink);
                yield return span;
                break;

            case HtmlInline:
            case HtmlEntityInline:
                // Igual que con los bloques HTML: no se escupe el marcado.
                break;

            default:
                yield return new Run(inline.ToString() ?? string.Empty);
                break;
        }
    }

    private static WpfInline InlineCode(string text)
    {
        var run = new Run(text) { FontSize = SmallFontSize };

        Size(run, "FontSize.Small", SmallFontSize);
        Family(run, "Font.Mono", "Cascadia Code, Consolas, Courier New");
        Theme(run, TextElement.BackgroundProperty, "ControlFillColorDefaultBrush", Brushes.WhiteSmoke);
        return run;
    }

    private static WpfInline Emphasis(EmphasisInline emphasis, Action<string>? openLink)
    {
        var span = new Span();
        Fill(span.Inlines, emphasis, openLink);

        switch (emphasis.DelimiterChar)
        {
            case '~':
                span.TextDecorations = TextDecorations.Strikethrough;
                break;
            case '*' or '_' when emphasis.DelimiterCount >= 2:
                span.FontWeight = FontWeights.SemiBold;
                break;
            default:
                span.FontStyle = FontStyles.Italic;
                break;
        }

        return span;
    }

    private static WpfInline Link(LinkInline link, Action<string>? openLink)
    {
        var span = new Span();
        Fill(span.Inlines, link, openLink);
        string text = new TextRange(span.ContentStart, span.ContentEnd).Text;
        return Link(string.IsNullOrWhiteSpace(text) ? link.Url ?? string.Empty : text, link.Url, openLink);
    }

    /// <summary>
    /// Un enlace del informe. Se pinta como enlace y, al pulsarlo, sale FUERA: quien llama lo abre
    /// en el navegador. Sin destino utilizable se queda en texto normal — un enlace que no lleva a
    /// ningún sitio es peor que una palabra.
    /// </summary>
    private static WpfInline Link(string text, string? url, Action<string>? openLink)
    {
        if (string.IsNullOrWhiteSpace(url) || openLink is null)
        {
            return new Run(text);
        }

        var hyperlink = new Hyperlink(new Run(text)) { ToolTip = url };
        Theme(hyperlink, TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush", Brushes.SteelBlue);
        hyperlink.Click += (_, _) => openLink(url);
        return hyperlink;
    }

    // ---------- Gravedad ----------

    /// <summary>
    /// LAS GRAVEDADES SE PINTAN CON LA PASTILLA DEL SISTEMA (F26 Parte C).
    /// <para>
    /// El informe las escribe de dos maneras y solo de esas dos: entre corchetes al abrir un
    /// hallazgo —<c>#### [Crítica] …</c>— y contadas en la línea de resumen de F23 —<c>Gravedad: 3
    /// Críticas · 27 Altas</c>—. En un texto corrido las dos se leen igual que el resto de la
    /// línea, que es justo lo que la gravedad no puede hacer: en Portafolio, en Hallazgos y en
    /// Métricas se ve sin leer, y en el informe —que es donde se decide qué arreglar— no.
    /// </para>
    /// <para>
    /// <b>El informe NO cambia</b> (F23, y es de lo que esta parte no toca): cambia cómo se pinta
    /// lo que ya dice. Por eso el reconocimiento es de las dos formas literales y de ninguna más —
    /// pillar «Baja» suelto convertiría en pastilla la palabra de cualquier frase que la use.
    /// </para>
    /// </summary>
    private static IEnumerable<WpfInline> Severities(string text)
    {
        foreach ((string piece, string? severity) in SeveritySpans(text))
        {
            // Sin `Application` viva no se construye un control: un `TextBlock` exige hilo STA y
            // los tests montan el documento en cualquier hilo. Lo que sale entonces es el texto
            // tal cual, que es lo que ya salía antes — la pastilla es presentación, no contenido.
            yield return severity is null || Application.Current is null
                ? new Run(piece)
                : Pill(severity, piece);
        }
    }

    /// <summary>
    /// El texto partido en trozos, cada uno con la gravedad que representa (o null si es texto
    /// normal). Separado del dibujo para poder comprobar la REGLA —qué se reconoce como gravedad y
    /// qué no— sin levantar una ventana.
    /// </summary>
    internal static IEnumerable<(string Text, string? Severity)> SeveritySpans(string text)
    {
        MatchCollection marks = SeverityMarks.Matches(text);
        if (marks.Count == 0)
        {
            yield return (text, null);
            yield break;
        }

        int at = 0;
        foreach (Match m in marks)
        {
            if (m.Index > at)
            {
                yield return (text[at..m.Index], null);
            }

            yield return m.Groups["b"].Success
                ? (m.Groups["b"].Value, m.Groups["b"].Value)
                : ($"{m.Groups["n"].Value} {m.Groups["w"].Value}", m.Groups["w"].Value);

            at = m.Index + m.Length;
        }

        if (at < text.Length)
        {
            yield return (text[at..], null);
        }
    }

    /// <summary>
    /// Una pastilla de gravedad dentro del texto. Los mismos pares que <c>Pill.Sev</c> en
    /// <c>Styles.xaml</c> —fondo teñido y tinta de la severidad—, pedidos por clave para que sigan
    /// al tema: un pincel resuelto aquí se quedaría con los colores del tema que hubiera al abrir
    /// el informe (D-971).
    /// </summary>
    private static WpfInline Pill(string severity, string label)
    {
        (string soft, string ink) = severity switch
        {
            // Cuatro rellenos para cuatro niveles (P-05, UI-0034): crítica y alta compartían
            // `Danger.Soft` —la escala se leía como tres— y la baja se pintaba del azul de
            // «estás aquí». Cada nivel tiene ahora el suyo, igual que en `Pill.Sev`.
            "Crítica" or "Críticas" or "Critica" or "Criticas" => ("Brush.Sev.Crit.Soft", "Brush.Sev.Crit"),
            "Alta" or "Altas" => ("Brush.Sev.High.Soft", "Brush.Sev.High"),
            "Media" or "Medias" => ("Brush.Sev.Med.Soft", "Brush.Sev.Med"),
            _ => ("Brush.Sev.Low.Soft", "Brush.Sev.Low"),
        };

        var caption = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        };
        Theme(caption, TextBlock.ForegroundProperty, ink, Brushes.Black);
        Size(caption, "FontSize.Meta", 13);

        var chip = new Border
        {
            Child = caption,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(0, 0, 2, 0),
        };
        Theme(chip, Border.BackgroundProperty, soft, Brushes.Transparent);

        return new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center };
    }

    // ---------- Tema ----------

    /// <summary>
    /// Enlaza una propiedad al pincel del tema. Sin <c>Application</c> viva —los tests— se pone el
    /// color de respaldo: el documento se puede construir y comprobar fuera de una ventana.
    /// </summary>
    /// <summary>
    /// Un tamaño de la escala (<c>Tokens.xaml</c>), por referencia de recurso. Es lo que hace que
    /// el informe siga la escala del sistema en vez de llevar la suya escrita: hasta F26 §C tenía
    /// siete números propios —13,5 de cuerpo y seis de encabezado— y por eso era el texto más
    /// pequeño de la aplicación siendo el más largo. Sin <c>Application</c> viva vale el número.
    /// </summary>
    private static void Size(DependencyObject element, string key, double fallback)
    {
        if (Application.Current is not null && element is FrameworkContentElement content)
        {
            content.SetResourceReference(TextElement.FontSizeProperty, key);
            return;
        }

        if (Application.Current is not null && element is FrameworkElement fe)
        {
            fe.SetResourceReference(TextBlock.FontSizeProperty, key);
            return;
        }

        element.SetValue(TextElement.FontSizeProperty, fallback);
    }

    /// <summary>La familia, por clave. Ver <see cref="Size"/>: mismo motivo y mismo respaldo.</summary>
    private static void Family(DependencyObject element, string key, string fallback)
    {
        if (Application.Current is not null && element is FrameworkContentElement content)
        {
            content.SetResourceReference(TextElement.FontFamilyProperty, key);
            return;
        }

        if (Application.Current is not null && element is FrameworkElement fe)
        {
            fe.SetResourceReference(TextBlock.FontFamilyProperty, key);
            return;
        }

        element.SetValue(TextElement.FontFamilyProperty, new FontFamily(fallback));
    }

    /// <summary>El interlineado, por clave. WPF lo quiere absoluto, y la escala ya lo declara así.</summary>
    private static void Line(DependencyObject element, string key, double fallback)
    {
        if (Application.Current is not null && element is FrameworkContentElement content)
        {
            content.SetResourceReference(WpfBlock.LineHeightProperty, key);
            return;
        }

        element.SetValue(WpfBlock.LineHeightProperty, fallback);
    }

    private static void Theme(DependencyObject element, DependencyProperty property, string key, Brush fallback)
    {
        if (Application.Current is not null && element is FrameworkContentElement content)
        {
            content.SetResourceReference(property, key);
            return;
        }

        if (Application.Current is not null && element is FrameworkElement fe)
        {
            fe.SetResourceReference(property, key);
            return;
        }

        element.SetValue(property, fallback);
    }
}
