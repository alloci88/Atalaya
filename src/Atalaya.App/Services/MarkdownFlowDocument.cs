using System.Windows;
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
    private const double BaseFontSize = 13.5;

    /// <summary>Los tamaños de los seis niveles de encabezado, de H1 a H6.</summary>
    private static readonly double[] HeadingSizes = { 23, 19, 16.5, 15, 14, 13.5 };

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
    public static FlowDocument Build(string? markdown, Action<string>? openLink = null)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = BaseFontSize,
            LineHeight = BaseFontSize * 1.55,
            PagePadding = new Thickness(0, 0, 14, 24),
            // Un ancho de columna infinito: el visor tiene su propio scroll y el informe se lee
            // en una sola columna, no en dos como haría el reparto por defecto.
            ColumnWidth = double.PositiveInfinity,
            Background = Brushes.Transparent,
        };

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
        var paragraph = new Paragraph
        {
            FontSize = HeadingSizes[level - 1],
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level == 1 ? 0 : 18, 0, 6),
        };

        Fill(paragraph.Inlines, heading.Inline, openLink);

        // Una raya bajo H1 y H2 separa las secciones largas sin necesidad de más espacio en
        // blanco, que es lo que un informe de tres pantallas agradece.
        if (level <= 2)
        {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 5);
            Theme(paragraph, WpfBlock.BorderBrushProperty,
                "CardStrokeColorDefaultBrush", Brushes.LightGray);
        }

        return paragraph;
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
        var paragraph = new Paragraph(new Run(TextOf(block)))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = BaseFontSize - 1.5,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(12, 9, 12, 9),
            BorderThickness = new Thickness(1),
            LineHeight = BaseFontSize * 1.35,
        };

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
                yield return new Run(literal.Content.ToString());
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
        var run = new Run(text)
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = BaseFontSize - 1,
        };

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

    // ---------- Tema ----------

    /// <summary>
    /// Enlaza una propiedad al pincel del tema. Sin <c>Application</c> viva —los tests— se pone el
    /// color de respaldo: el documento se puede construir y comprobar fuera de una ventana.
    /// </summary>
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
