using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atalaya.App.Services;

/// <summary>
/// El trozo de fichero que se enseña en la ficha: de qué línea real a qué línea real, y cómo se
/// llama lo que hay dentro. Las líneas son <b>1-based</b> y del fichero, no del recorte.
/// </summary>
/// <param name="StartLine">Primera línea del fichero incluida (1-based).</param>
/// <param name="EndLine">Última línea incluida (1-based, inclusive).</param>
/// <param name="Member">
/// El miembro que contiene la línea (<c>Clase.Metodo</c>), o <c>null</c> si el recorte es una
/// ventana ciega alrededor de la línea porque no se pudo derivar ninguno.
/// </param>
public sealed record CodeSpanLines(int StartLine, int EndLine, string? Member)
{
    /// <summary>Salió de un miembro de verdad, no del margen fijo.</summary>
    public bool IsMember => Member is not null;

    public int LineCount => EndLine - StartLine + 1;
}

/// <summary>
/// Los límites del <b>método/miembro completo</b> que contiene una línea (F5.5 §3).
/// <para>
/// <b>Por qué.</b> El snippet de la ficha era un recorte ciego —cuatro líneas antes y tres
/// después— que cortaba la firma por la mitad y dejaba fuera el <c>using</c> o el <c>return</c>
/// que explicaban el hallazgo. Un recorte arbitrario obliga a abrir el editor para entender lo
/// que la ficha acaba de afirmar.
/// </para>
/// <para>
/// <b>Por qué el parser y no contar llaves.</b> Contar llaves se equivoca con las que viven dentro
/// de cadenas, de comentarios, de literales de carácter, de cadenas crudas (<c>"""</c>) y de
/// interpolaciones anidadas; y esos casos no son exóticos en el código que audita Atalaya. Roslyn
/// resuelve todo eso de una vez y además <b>tolera ficheros que no compilan</b>: el árbol sale
/// igual con nodos de error, así que un fichero a medio editar sigue dando un miembro.
/// </para>
/// <para>
/// Para lo que no es C#, o cuando la línea cae fuera de todo miembro (usings, cabecera del
/// fichero, un <c>namespace</c> suelto), se devuelve la ventana de ±<see cref="FallbackRadius"/>
/// líneas, que es exactamente lo que §3 pide como plan B.
/// </para>
/// </summary>
public static class MethodBoundary
{
    /// <summary>Las líneas a cada lado cuando no hay miembro del que sacar los límites.</summary>
    public const int FallbackRadius = 15;

    /// <summary>
    /// Un miembro más largo que esto no se enseña entero: el panel acabaría siendo el fichero. Se
    /// recorta a una ventana centrada en la línea del hallazgo, que es lo que se estaba mirando.
    /// </summary>
    public const int MaxMemberLines = 120;

    /// <summary>¿Merece la pena parsear este fichero como C#?</summary>
    public static bool IsCSharp(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Los límites del recorte a enseñar para <paramref name="line"/> (1-based) dentro de
    /// <paramref name="source"/>. Nunca lanza: cualquier tropiezo cae en la ventana fija.
    /// </summary>
    public static CodeSpanLines ForLine(string source, int line, string path = "x.cs")
    {
        string[] lines = (source ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');
        return ForLine(lines, line, path);
    }

    /// <inheritdoc cref="ForLine(string,int,string)"/>
    public static CodeSpanLines ForLine(IReadOnlyList<string> lines, int line, string path = "x.cs")
    {
        int total = Math.Max(lines.Count, 1);
        int target = Math.Clamp(line, 1, total);

        if (IsCSharp(path))
        {
            CodeSpanLines? member = TryMember(string.Join("\n", lines), target, total);
            if (member is not null)
            {
                return member;
            }
        }

        return Window(target, total);
    }

    /// <summary>La ventana ciega de ±<see cref="FallbackRadius"/>, recortada al fichero.</summary>
    public static CodeSpanLines Window(int line, int totalLines)
    {
        int total = Math.Max(totalLines, 1);
        int target = Math.Clamp(line, 1, total);
        return new CodeSpanLines(
            Math.Max(1, target - FallbackRadius),
            Math.Min(total, target + FallbackRadius),
            null);
    }

    private static CodeSpanLines? TryMember(string source, int line, int totalLines)
    {
        try
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
            SourceText text = tree.GetText();
            if (line > text.Lines.Count)
            {
                return null;
            }

            // El primer carácter de la línea del hallazgo; desde ahí se sube por el árbol.
            int offset = text.Lines[line - 1].Start;
            SyntaxNode root = tree.GetRoot();
            SyntaxNode? node = root.FindNode(new TextSpan(offset, 0), getInnermostNodeForTie: true);

            SyntaxNode? member = Enclosing(node);
            if (member is null)
            {
                return null;
            }

            FileLinePositionSpan span = tree.GetLineSpan(member.Span);
            int start = span.StartLinePosition.Line + 1;
            int end = Math.Min(totalLines, span.EndLinePosition.Line + 1);
            if (end < start || line < start || line > end)
            {
                return null;
            }

            string? name = NameOf(member);
            if (name is null)
            {
                return null;
            }

            if (end - start + 1 > MaxMemberLines)
            {
                // Un miembro gigantesco: se enseña la parte que rodea al hallazgo, sin mentir
                // sobre los números de línea, y conservando el nombre del miembro.
                int half = MaxMemberLines / 2;
                return new CodeSpanLines(
                    Math.Max(start, line - half),
                    Math.Min(end, line + half),
                    name);
            }

            return new CodeSpanLines(start, end, name);
        }
        catch
        {
            // Un parser que se cae no puede tumbar la ficha: se enseña la ventana fija.
            return null;
        }
    }

    /// <summary>
    /// El miembro más cercano hacia arriba. Una función local dentro de un método se prefiere al
    /// método entero: es el trozo que explica el hallazgo. Un tipo entero NO cuenta como miembro
    /// —enseñar la clase completa es enseñar el fichero—, así que una línea que cae en la
    /// declaración de la clase se queda sin miembro y acaba en la ventana fija.
    /// </summary>
    private static SyntaxNode? Enclosing(SyntaxNode? node)
    {
        for (SyntaxNode? n = node; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case LocalFunctionStatementSyntax:
                case AccessorDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case DestructorDeclarationSyntax:
                case MethodDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case EventDeclarationSyntax:
                case FieldDeclarationSyntax:
                case EventFieldDeclarationSyntax:
                case EnumMemberDeclarationSyntax:
                case DelegateDeclarationSyntax:
                case GlobalStatementSyntax:
                    return n;
            }
        }

        return null;
    }

    /// <summary>Cómo se llama, con el tipo que lo contiene delante cuando se sabe.</summary>
    private static string? NameOf(SyntaxNode member)
    {
        string? own = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            LocalFunctionStatementSyntax l => l.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            EventDeclarationSyntax e => e.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
            ConversionOperatorDeclarationSyntax => "operator",
            DelegateDeclarationSyntax dg => dg.Identifier.Text,
            EnumMemberDeclarationSyntax em => em.Identifier.Text,
            AccessorDeclarationSyntax a => AccessorName(a),
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            GlobalStatementSyntax => "(nivel superior)",
            _ => null,
        };

        if (own is null)
        {
            return null;
        }

        // Con el tipo delante se lee de un vistazo de qué clase es este método.
        for (SyntaxNode? n = member.Parent; n is not null; n = n.Parent)
        {
            if (n is TypeDeclarationSyntax type)
            {
                return $"{type.Identifier.Text}.{own}";
            }
        }

        return own;
    }

    /// <summary>El accesor con su propiedad delante: <c>Nombre.get</c>.</summary>
    private static string AccessorName(AccessorDeclarationSyntax a)
    {
        string kind = a.Keyword.Text;
        SyntaxNode? owner = a.Parent?.Parent;
        return owner switch
        {
            PropertyDeclarationSyntax p => $"{p.Identifier.Text}.{kind}",
            IndexerDeclarationSyntax => $"this[].{kind}",
            EventDeclarationSyntax e => $"{e.Identifier.Text}.{kind}",
            _ => kind,
        };
    }
}
