using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static CodecApiEmitter;

internal sealed class AsyncContextEmitter
{
    private readonly HashSet<ITypeSymbol> types = new(SymbolEqualityComparer.Default);
    internal IEnumerable<ITypeSymbol> KnownTypes => types;

    internal bool Register(ITypeSymbol type)
    {
        if (!ContainsAsync(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))) return false;
        types.Add(type.WithNullableAnnotation(NullableAnnotation.None));
        return true;
    }

    internal bool RegisterUnsupported(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol || type.IsRefLikeType || type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error)
            return false;
        types.Add(type.WithNullableAnnotation(NullableAnnotation.None));
        return true;
    }

    private static bool ContainsAsync(ITypeSymbol type, HashSet<ITypeSymbol> visited)
    {
        if (!visited.Add(type)) return false;
        if (type is IArrayTypeSymbol array) return ContainsAsync(array.ElementType, visited);
        if (type is not INamedTypeSymbol named) return false;
        if (named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>") return true;
        if (named.TypeArguments.Any(argument => ContainsAsync(argument, visited))) return true;
        if (named.ContainingAssembly.Identity.Name.StartsWith("System", StringComparison.Ordinal) ||
            named.SpecialType != SpecialType.None) return false;
        return named.GetMembers().OfType<IPropertySymbol>().Where(property => !property.IsStatic &&
            property.DeclaredAccessibility == Accessibility.Public).Any(property => ContainsAsync(property.Type, visited));
    }

    internal void Write(string directory)
    {
        if (types.Count == 0) return;
        var document = Cs.Document(CsLanguageVersion.CSharp14).Nullable(NullableMode.Enable);
        var context = document.Class("GeneratedAsyncJsonContext").Internal().Partial()
            .Base(Cs.Type("System.Text.Json.Serialization.JsonSerializerContext"));
        var options = Cs.Type("System.Text.Json.JsonSerializerOptions");
        context.Field(Cs.Generic("System.Runtime.CompilerServices.ConditionalWeakTable", options,
            Cs.Type("GeneratedAsyncJsonContext")), "Contexts").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var create = Cs.Lambda(new CsBody
        {
            Tree = Cs.Tree(Return(NodeNew("GeneratedAsyncJsonContext",
            NodeNew("System.Text.Json.JsonSerializerOptions", Cs.Symbol("value")))))
        }, Cs.LambdaParameter("value"))
            with
        { IsStatic = true };
        context.Method(Cs.Type("GeneratedAsyncJsonContext"), "ForOptions").Internal().Static()
            .Parameter(options.Nullable(), "options").Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol("options")),
                Return(Cs.Symbol("Default"))), Return(NodeCall("Contexts.GetValue", Cs.Symbol("options"), create)))).End();
        foreach (var type in types)
        {
            var typeOf = new CsExpression(ExpressionKind.TypeOf, "", [])
            { Type = CodecApiEmitter.NodeType(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) };
            context.Attribute(Cs.Attribute(Cs.Type("System.Text.Json.Serialization.JsonSerializable"), Cs.Arg(typeOf)));
        }
        context.End();
        try { File.WriteAllText(Path.Combine(directory, "JsonCodecsAsyncContext.g.cs"), document.Render()); }
        catch (Neemle.CSharpComposer.Validation.GenValidationException)
        {
            int index = 0;
            foreach (var type in types) Console.Error.WriteLine("Generated context type " + index++ + ": " + type);
            throw;
        }
    }
}
