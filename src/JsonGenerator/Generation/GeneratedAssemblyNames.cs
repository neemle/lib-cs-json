using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Neemle.Json.Generator;

internal sealed class GeneratedAssemblyNames(string suffix) : CSharpSyntaxRewriter
{
    internal static string Rename(string source, string assemblyName, CSharpParseOptions options)
    {
        string suffix = Suffix(assemblyName);
        var root = CSharpSyntaxTree.ParseText(source, options).GetRoot();
        return new GeneratedAssemblyNames(suffix).Visit(root)!.ToFullString();
    }

    internal static string Name(string name, string assemblyName) => name + Suffix(assemblyName);

    private static string Suffix(string assemblyName) =>
        "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assemblyName)))[..16];

    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        if (!token.IsKind(SyntaxKind.IdentifierToken) || !IsGeneratedName(token.ValueText))
            return base.VisitToken(token);
        return SyntaxFactory.Identifier(token.LeadingTrivia, token.ValueText + suffix, token.TrailingTrivia);
    }

    private static bool IsGeneratedName(string name) => name == "GeneratedJson" ||
        name.StartsWith("GeneratedJson_", StringComparison.Ordinal) ||
        name.StartsWith("GeneratedJsonCodec_", StringComparison.Ordinal);
}
