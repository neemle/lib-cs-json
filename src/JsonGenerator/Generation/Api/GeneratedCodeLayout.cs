using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neemle.Json.Generator;

internal static class GeneratedCodeLayout
{
    internal const string GeneratedTypeName = "GeneratedJson";

    private const int MaxTypeLines = 500;
    private const int MaxMethodLines = 30;
    private const int MaxComplexity = 10;
    private const int MaxLineLength = 120;

    internal static IReadOnlyList<string> Split(string source)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
        var type = root.Members.OfType<ClassDeclarationSyntax>().Single();
        return GroupMembers(root, type);
    }

    private static List<string> GroupMembers(CompilationUnitSyntax root, ClassDeclarationSyntax type)
    {
        var groups = SplitCodecs(type);
        var parts = new List<(string Name, List<MemberDeclarationSyntax> Members)>();
        foreach (var (name, members) in groups) PartitionGroup(root, type, name, members, parts);
        var owners = parts.SelectMany(part => part.Members.SelectMany(MemberNames)
            .Select(member => (Member: member, Owner: part.Name))).ToLookup(item => item.Member, item => item.Owner);
        return parts.Select(part => Validate(FormatWithImports(root, type, part.Name, part.Members, owners))).ToList();
    }

    private static IEnumerable<string> MemberNames(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => [method.Identifier.ValueText],
        FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText),
        TypeDeclarationSyntax type => [type.Identifier.ValueText],
        PropertyDeclarationSyntax property => [property.Identifier.ValueText],
        _ => []
    };

    private static Dictionary<string, List<MemberDeclarationSyntax>> SplitCodecs(ClassDeclarationSyntax type)
    {
        var groups = new Dictionary<string, List<MemberDeclarationSyntax>>();
        string name = GeneratedTypeName;
        groups[name] = [];
        foreach (var member in type.Members)
        {
            if (member is ClassDeclarationSyntax nested && nested.Identifier.ValueText.StartsWith("Holder_",
                StringComparison.Ordinal))
            {
                name = "GeneratedJsonCodec_" + nested.Identifier.ValueText["Holder_".Length..];
                groups[name] = [];
            }
            groups[name].Add(Expose(member));
        }
        return groups;
    }

    private static void PartitionGroup(CompilationUnitSyntax root, ClassDeclarationSyntax type, string name,
        List<MemberDeclarationSyntax> group, List<(string Name, List<MemberDeclarationSyntax> Members)> parts)
    {
        var members = new List<MemberDeclarationSyntax>();
        int part = 0;
        int overhead = TypeLineCount(Format(root, type, PartName(name, part), []));
        int lines = overhead;
        foreach (var member in group)
        {
            int memberLines = TypeLineCount(Format(root, type, PartName(name, part), [member])) - overhead;
            if (memberLines + overhead > MaxTypeLines)
                throw new InvalidOperationException("Generated type member exceeds 500 lines.");
            if (lines + memberLines > MaxTypeLines)
            {
                parts.Add((PartName(name, part++), [.. members]));
                members.Clear();
                lines = overhead;
            }
            members.Add(member);
            lines += memberLines;
        }
        if (members.Count != 0) parts.Add((PartName(name, part), members));
    }

    private static string PartName(string name, int part) => part == 0 ? name : name + "_Part" + part;

    private static MemberDeclarationSyntax Expose(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.WithModifiers(Expose(method.Modifiers)),
        FieldDeclarationSyntax field => field.WithModifiers(Expose(field.Modifiers)),
        ClassDeclarationSyntax type => type.WithModifiers(Expose(type.Modifiers)),
        _ => member
    };

    private static SyntaxTokenList Expose(SyntaxTokenList modifiers)
    {
        var token = modifiers.FirstOrDefault(item => item.IsKind(SyntaxKind.PrivateKeyword));
        return token == default ? modifiers : modifiers.Replace(token,
            SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTriviaFrom(token));
    }

    private static string FormatWithImports(CompilationUnitSyntax root, ClassDeclarationSyntax type,
        string name, List<MemberDeclarationSyntax> members, ILookup<string, string> owners)
    {
        var imports = members.SelectMany(member => member.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            .SelectMany(identifier => owners[identifier.Identifier.ValueText]).Where(owner => owner != name)
            .Distinct().Select(StaticImport);
        return Format(root.WithUsings(root.Usings.AddRange(imports)), type, name, members);
    }

    private static UsingDirectiveSyntax StaticImport(string name) => SyntaxFactory
        .ParseCompilationUnit("using static global::" + name + ";\n").Usings[0]
        .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

    private static string Format(CompilationUnitSyntax root, ClassDeclarationSyntax type, string name,
        List<MemberDeclarationSyntax> members)
    {
        var renamed = type.WithIdentifier(SyntaxFactory.Identifier(name).WithTriviaFrom(type.Identifier))
            .WithMembers(SyntaxFactory.List(members));
        var unit = root.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(renamed));
        return Wrap(unit.ToFullString());
    }

    private static string Wrap(string source)
    {
        for (int pass = 0; pass < MaxLineLength; pass++)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var tokens = tree.GetRoot().DescendantTokens().ToArray();
            var edits = new List<int>();
            foreach (var line in tree.GetText().Lines.Where(line => line.Span.Length > MaxLineLength))
                edits.Add(FindBreak(source, line.Start, line.End, tokens));
            if (edits.Count == 0) return source;
            foreach (int position in edits.OrderDescending()) source = source.Insert(position, "\n    ");
        }
        throw new InvalidOperationException("Generated code could not be wrapped to 120 characters.");
    }

    private static int FindBreak(string source, int start, int end, SyntaxToken[] tokens)
    {
        var candidates = tokens.Where(token => token.SpanStart > start && token.SpanStart <= start + 108 &&
            token.SpanStart < end &&
            !token.Parent!.AncestorsAndSelf().OfType<InterpolatedStringExpressionSyntax>().Any() &&
            (char.IsWhiteSpace(source[token.SpanStart - 1]) ||
             token.IsKind(SyntaxKind.DotToken) && source[token.SpanStart - 1] != '?')).ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException(
                "Generated source contains a token that cannot fit within 120 characters.");
        return candidates[^1].SpanStart;
    }

    private static string Validate(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Generated C# has a syntax error.");
        if (TypeLineCount(source) > MaxTypeLines)
            throw new InvalidOperationException("Generated type exceeds 500 lines.");
        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            ValidateMethod(method, tree);
        return source;
    }

    private static void ValidateMethod(BaseMethodDeclarationSyntax method, SyntaxTree tree)
    {
        var descendants = method.DescendantNodes(node => node == method ||
            node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).ToArray();
        int complexity = 1 + descendants.Count(node => node is IfStatementSyntax or ForStatementSyntax or
            ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax or
            ConditionalExpressionSyntax or SwitchExpressionArmSyntax or CaseSwitchLabelSyntax);
        int nesting = descendants.OfType<BlockSyntax>().Select(block => block.Ancestors().TakeWhile(a => a != method)
            .OfType<BlockSyntax>().Count()).DefaultIfEmpty().Max();
        bool constructorRead = method is MethodDeclarationSyntax declaration &&
            declaration.Identifier.ValueText.StartsWith("ReadCore_", StringComparison.Ordinal);
        bool constructorAccessor = method is MethodDeclarationSyntax accessor &&
            accessor.Identifier.ValueText.StartsWith("Access_constructor_", StringComparison.Ordinal);
        if ((!constructorRead && LineCount(tree, method.Span) > MaxMethodLines) || complexity > MaxComplexity || nesting > 4 ||
            (!constructorAccessor && method.ParameterList.Parameters.Count > 5))
            throw new InvalidOperationException(
                "Generated JSON operation exceeds the permitted code limits: " + method);
    }

    private static int TypeLineCount(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var type = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        return LineCount(tree, type.Span);
    }

    private static int LineCount(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        var lines = tree.GetText().Lines;
        var range = tree.GetLineSpan(span);
        int start = range.StartLinePosition.Line;
        int count = range.EndLinePosition.Line - start + 1;
        return Enumerable.Range(start, count).Count(index => !string.IsNullOrWhiteSpace(lines[index].ToString()));
    }
}
