using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neemle.Json.Generator;

internal sealed class GeneratedAssemblyNames(string suffix, bool rewrittenSource) : CSharpSyntaxRewriter
{
    internal static void Rewrite(string directory, string assemblyName, CSharpParseOptions options)
    {
        string suffix = "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assemblyName)))[..16];
        foreach (string path in Directory.EnumerateFiles(directory, "*.g.cs"))
        {
            bool rewrittenSource = Path.GetFileName(path).StartsWith("Source", StringComparison.Ordinal);
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), options).GetRoot();
            var renamed = new GeneratedAssemblyNames(suffix, rewrittenSource).Visit(root)!;
            File.WriteAllText(path, renamed.ToFullString());
        }
    }

    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        if (!token.IsKind(SyntaxKind.IdentifierToken) || !IsGeneratedName(token.ValueText))
            return base.VisitToken(token);
        if (rewrittenSource && (token.Parent is not IdentifierNameSyntax name ||
            name.Parent is not AliasQualifiedNameSyntax alias || alias.Alias.Identifier.ValueText != "global" ||
            alias.Name != name)) return base.VisitToken(token);
        return SyntaxFactory.Identifier(token.LeadingTrivia, token.ValueText + suffix, token.TrailingTrivia);
    }

    private static bool IsGeneratedName(string name) => name == "GeneratedJson" ||
        name.StartsWith("GeneratedJson_", StringComparison.Ordinal) ||
        name.StartsWith("GeneratedJsonCodec_", StringComparison.Ordinal);
}
