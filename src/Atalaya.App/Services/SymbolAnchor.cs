using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atalaya.App.Services;

/// <summary>Dónde ha aparecido un miembro y cómo se llama. <c>Line = 0</c> es «no está».</summary>
public sealed record SymbolHit(int Line, string Member)
{
    public static readonly SymbolHit None = new(0, string.Empty);

    public bool Found => Line > 0;
}

/// <summary>
/// Re-anclaje por <b>símbolo</b> cuando el hash ya no casa (F5.6 §2, D-222/D-224).
/// <para>
/// <b>Es el plan B, a propósito.</b> El hash identifica el texto exacto que se auditó; el símbolo
/// solo dice «el problema estaba en este método». Cuando el código de dentro cambia pero el método
/// sigue ahí, el símbolo es lo único que queda, y es muchísimo mejor que seguir señalando un
/// número de línea que ya no significa nada.
/// </para>
/// <para>
/// <b>Y nunca resalta un comentario.</b> La línea buena de un miembro es su primera línea de
/// código ejecutable —el primer statement, o la expresión de un miembro de expresión—; si no
/// tiene cuerpo, su declaración. La documentación XML y los atributos son trivia y nunca son el
/// resaltado: ese era justo el fallo que hacía apuntar a <c>/// &lt;param name="detId"&gt;</c> un
/// hallazgo cuyo código estaba cinco líneas más abajo.
/// </para>
/// </summary>
public static partial class SymbolAnchor
{
    /// <summary>
    /// La línea a resaltar del miembro que se llame como alguno de <paramref name="names"/>, en
    /// el orden en que vienen (el símbolo declarado antes que lo adivinado del título).
    /// </summary>
    public static SymbolHit FindMember(IReadOnlyList<string> lines, string path, IReadOnlyList<string> names)
    {
        if (names.Count == 0 || !MethodBoundary.IsCSharp(path) || lines.Count == 0)
        {
            return SymbolHit.None;
        }

        try
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(string.Join("\n", lines));
            SyntaxNode root = tree.GetRoot();

            foreach (string name in names)
            {
                foreach (SyntaxNode node in root.DescendantNodes())
                {
                    if (!IsMember(node) || !string.Equals(SimpleName(node), name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int line = AnchorLine(tree, node, lines.Count);
                    if (line > 0)
                    {
                        return new SymbolHit(line, MemberLabel(node));
                    }
                }
            }
        }
        catch (Exception)
        {
            // Un parser que se cae no puede tumbar la ficha: se responde «no está».
            return SymbolHit.None;
        }

        return SymbolHit.None;
    }

    /// <summary>
    /// La primera línea de código del miembro que contiene <paramref name="line"/>. Si la línea ya
    /// es código, se devuelve tal cual; si es documentación, un atributo, una llave o un blanco, se
    /// baja a la primera que sí lo sea. Sin miembro alrededor, se devuelve la de entrada.
    /// </summary>
    public static int FirstCodeLine(IReadOnlyList<string> lines, string path, int line)
    {
        if (!MethodBoundary.IsCSharp(path) || lines.Count == 0)
        {
            return line;
        }

        int target = Math.Clamp(line, 1, lines.Count);
        try
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(string.Join("\n", lines));
            SourceText text = tree.GetText();
            if (target > text.Lines.Count)
            {
                return line;
            }

            SyntaxNode? node = tree.GetRoot()
                .FindNode(new TextSpan(text.Lines[target - 1].Start, 0), getInnermostNodeForTie: true);
            SyntaxNode? member = Enclosing(node);
            if (member is null)
            {
                return line;
            }

            FileLinePositionSpan span = tree.GetLineSpan(member.Span);
            int start = span.StartLinePosition.Line + 1;
            int end = span.EndLinePosition.Line + 1;

            // Dentro del cuerpo y sobre código: la línea del auditor ya era buena.
            if (target > start && target < end && IsCode(lines[target - 1]))
            {
                return target;
            }

            int anchored = AnchorLine(tree, member, lines.Count);
            return anchored > 0 ? anchored : line;
        }
        catch (Exception)
        {
            return line;
        }
    }

    /// <summary>
    /// Los nombres de miembro que un hallazgo conoce, del más fiable al menos: primero el
    /// <c>symbol</c> que declaró el auditor, luego los identificadores del título que parecen
    /// nombres de miembro.
    /// <para>
    /// El título es una fuente pobre —por eso va detrás— pero es la única que tienen los hallazgos
    /// anteriores a que <c>Finding.Symbol</c> existiera (D-223). El filtro es deliberadamente
    /// estrecho: se prefieren los identificadores de varias jorobas (<c>ConvertToDetId</c>,
    /// <c>StringToByteArray</c>), que casi nunca son una palabra en castellano, y las de una sola
    /// joroba van al final por si acaso.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? symbol, string? title)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var strong = new List<string>();
        var weak = new List<string>();

        foreach (string part in SplitSymbol(symbol))
        {
            if (seen.Add(part))
            {
                strong.Add(part);
            }
        }

        foreach (Match m in IdentifierToken().Matches(title ?? string.Empty))
        {
            string id = m.Value;
            if (id.Length < 3 || !char.IsUpper(id[0]) || !seen.Add(id))
            {
                continue;
            }

            (MultiHump(id) ? strong : weak).Add(id);
        }

        strong.AddRange(weak);
        return strong;
    }

    /// <summary>Del <c>symbol</c> declarado salen sus partes, la más específica primero.</summary>
    private static IEnumerable<string> SplitSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            yield break;
        }

        string[] parts = symbol.Split(
            new[] { '.', '(', ')', ',', ' ', '<', '>', ':', '#' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string p = parts[i].Trim();
            if (p.Length >= 2 && IsIdentifier(p))
            {
                yield return p;
            }
        }
    }

    private static bool IsIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_')
           && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Más de una mayúscula, y alguna minúscula: huele a nombre de miembro, no a palabra.</summary>
    private static bool MultiHump(string id)
        => id.Count(char.IsUpper) >= 2 && id.Any(char.IsLower);

    /// <summary>La línea a resaltar de un miembro: su primer código, o su declaración.</summary>
    private static int AnchorLine(SyntaxTree tree, SyntaxNode member, int totalLines)
    {
        int line = BodyFirstLine(tree, member) ?? DeclarationLine(tree, member);
        return line >= 1 && line <= totalLines ? line : 0;
    }

    /// <summary>El primer statement o la expresión del cuerpo. <c>null</c> si el miembro no tiene.</summary>
    private static int? BodyFirstLine(SyntaxTree tree, SyntaxNode member)
    {
        SyntaxNode? first = member switch
        {
            BaseMethodDeclarationSyntax m
                => (SyntaxNode?)m.Body?.Statements.FirstOrDefault() ?? m.ExpressionBody?.Expression,
            LocalFunctionStatementSyntax l
                => (SyntaxNode?)l.Body?.Statements.FirstOrDefault() ?? l.ExpressionBody?.Expression,
            AccessorDeclarationSyntax a
                => (SyntaxNode?)a.Body?.Statements.FirstOrDefault() ?? a.ExpressionBody?.Expression,
            PropertyDeclarationSyntax p => FirstOfProperty(p),
            IndexerDeclarationSyntax ix
                => (SyntaxNode?)ix.ExpressionBody?.Expression ?? FirstOfAccessors(ix.AccessorList),
            FieldDeclarationSyntax f
                => f.Declaration.Variables.FirstOrDefault()?.Initializer?.Value,
            _ => null,
        };

        return first is null ? null : tree.GetLineSpan(first.Span).StartLinePosition.Line + 1;
    }

    private static SyntaxNode? FirstOfProperty(PropertyDeclarationSyntax p)
        => (SyntaxNode?)p.ExpressionBody?.Expression
           ?? FirstOfAccessors(p.AccessorList)
           ?? p.Initializer?.Value;

    private static SyntaxNode? FirstOfAccessors(AccessorListSyntax? accessors)
        => accessors?.Accessors
            .Select(a => (SyntaxNode?)a.Body?.Statements.FirstOrDefault() ?? a.ExpressionBody?.Expression)
            .FirstOrDefault(x => x is not null);

    /// <summary>
    /// La línea de la declaración, saltándose los atributos. La documentación XML no hace falta
    /// saltarla: es trivia, y <see cref="SyntaxNode.Span"/> ya la deja fuera.
    /// </summary>
    private static int DeclarationLine(SyntaxTree tree, SyntaxNode member)
    {
        foreach (SyntaxNodeOrToken child in member.ChildNodesAndTokens())
        {
            if (child.IsNode && child.AsNode() is AttributeListSyntax)
            {
                continue;
            }

            SyntaxToken token = child.IsToken ? child.AsToken() : child.AsNode()!.GetFirstToken();
            return tree.GetLineSpan(token.Span).StartLinePosition.Line + 1;
        }

        return tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;
    }

    /// <summary>¿Esta línea es código, o documentación / llave / blanco?</summary>
    private static bool IsCode(string line)
    {
        string t = line.Trim();
        if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)
            || t.StartsWith("/*", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)
            || t.StartsWith("[", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        return t.Trim('{', '}', ')', '(').Length > 0;
    }

    private static bool IsMember(SyntaxNode node) => Enclosing(node) == node;

    /// <summary>El mismo criterio de miembro que <see cref="MethodBoundary"/>, para no divergir.</summary>
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

    /// <summary>El nombre a secas del miembro, para comparar con los candidatos.</summary>
    private static string? SimpleName(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        LocalFunctionStatementSyntax l => l.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        DestructorDeclarationSyntax d => d.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        DelegateDeclarationSyntax dg => dg.Identifier.Text,
        EnumMemberDeclarationSyntax em => em.Identifier.Text,
        AccessorDeclarationSyntax a => (a.Parent?.Parent as PropertyDeclarationSyntax)?.Identifier.Text,
        FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };

    /// <summary>Cómo se le llama al usuario: <c>Clase.Miembro</c> cuando se sabe la clase.</summary>
    private static string MemberLabel(SyntaxNode member)
    {
        string own = SimpleName(member) ?? "(miembro)";
        for (SyntaxNode? n = member.Parent; n is not null; n = n.Parent)
        {
            if (n is TypeDeclarationSyntax type)
            {
                return $"{type.Identifier.Text}.{own}";
            }
        }

        return own;
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex IdentifierToken();
}
