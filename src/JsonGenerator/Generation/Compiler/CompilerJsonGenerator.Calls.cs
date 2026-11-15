using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

public sealed partial class CompilerJsonGenerator
{
    private static void AddHttpJsonInterceptor(Neemle.CSharpComposer.Builders.TypeBuilder<Neemle.CSharpComposer.Builders.FileBuilder> root,
        IMethodSymbol original, InterceptableLocation location, string assembly, int position)
    {
        var format = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        var method = root.Method(NodeType(original.ReturnType.ToDisplayString(format)), "Call" + position).Internal().Static()
            .Attribute(Cs.Attribute(Cs.Type("System.Runtime.CompilerServices.InterceptsLocation"),
                Cs.Arg(Cs.Literal(location.Version)), Cs.Arg(Cs.Literal(location.Data))))
            .Attribute(Cs.Attribute(Cs.Type("System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage"),
                Cs.Arg(Cs.Literal("Trimming")), Cs.Arg(Cs.Literal("IL2026"))))
            .Attribute(Cs.Attribute(Cs.Type("System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage"),
                Cs.Arg(Cs.Literal("AOT")), Cs.Arg(Cs.Literal("IL3050"))));
        bool extension = original.ReducedFrom is not null;
        if (extension) method.Parameter(NodeType(original.ReceiverType!.ToDisplayString(format)), "receiver", "this");
        for (int i = 0; i < original.Parameters.Length; i++)
            method.Parameter(NodeType(original.Parameters[i].Type.ToDisplayString(format)), "arg" + i);
        var arguments = new List<CsNode>();
        if (extension) arguments.Add(Cs.Symbol("receiver"));
        arguments.AddRange(original.Parameters.Select((_, index) => (CsNode)Cs.Symbol("arg" + index)));
        if (original.ContainingType.ToDisplayString() == "System.Net.Http.Json.HttpContentJsonExtensions")
        {
            int optionIndex = -1;
            for (int i = 0; i < original.Parameters.Length; i++)
                if (original.Parameters[i].Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() ==
                    "System.Text.Json.JsonSerializerOptions") optionIndex = i;
            CsNode options = optionIndex < 0 ? Cs.Value(null) : Cs.Symbol("arg" + optionIndex);
            var web = NodeNew("System.Text.Json.JsonSerializerOptions", NodeMember(TypeRef("System.Text.Json.JsonSerializerDefaults"), "Web"));
            var context = NodeCall(GeneratedAssemblyNames.Name("GeneratedAsyncJsonContext", assembly) + ".ForOptions",
                NodeBinary(CsOperator.Coalesce, options, web));
            var info = Cs.Symbol("GetTypeInfo", new() { Target = context, AsCall = true,
                Arguments = Cs.Compound(TypeOf(TypeName(original.TypeArguments[0]))) });
            var metadata = Cast("System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + TypeName(original.TypeArguments[0]) + ">", NonNull(info));
            if (optionIndex >= 0) arguments[optionIndex + 1] = metadata; else arguments.Insert(1, metadata);
        }
        var target = Cs.Symbol(original.Name, new() { Target = TypeRef(original.ContainingType.ToDisplayString()),
            TypeArguments = new[] { NodeType(TypeName(original.TypeArguments[0])) } });
        var call = Cs.Symbol(new() { AsCall = true, Target = target, Arguments = Cs.Compound(arguments.ToArray()) });
        method.Body(original.ReturnsVoid ? Cs.Tree(call) : Cs.Tree(Return(call))).End();
    }

    private static bool UsesRegisteredConverters(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        if (model.GetOperation(invocation) is not IInvocationOperation operation) return false;
        var argument = operation.Arguments.FirstOrDefault(item => item.Parameter?.Type
            .WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == "System.Text.Json.JsonSerializerOptions");
        if (argument?.Syntax is not ArgumentSyntax syntax) return false;
        if (syntax.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(item => item.Identifier.ValueText == "Converters")) return true;
        var symbol = model.GetSymbolInfo(syntax.Expression).Symbol;
        if (symbol is IFieldSymbol or IParameterSymbol or IPropertySymbol) return true;
        if (symbol is not ILocalSymbol) return false;
        return invocation.SyntaxTree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Any(member => member.Name.Identifier.ValueText == "Converters" &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(member.Expression).Symbol, symbol));
    }

    private static void AddNodeInterceptor(Neemle.CSharpComposer.Builders.TypeBuilder<Neemle.CSharpComposer.Builders.FileBuilder> root,
        IMethodSymbol original, InterceptableLocation location, string assembly, int index, int position)
    {
        string result = original.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string codec = GeneratedAssemblyNames.Name("GeneratedJsonCodec_" + index, assembly);
        root.Method(NodeType(result), "Call" + position).Internal().Static()
            .Attribute(Cs.Attribute(Cs.Type("System.Runtime.CompilerServices.InterceptsLocation"),
                Cs.Arg(Cs.Literal(location.Version)), Cs.Arg(Cs.Literal(location.Data))))
            .Parameter(Cs.Type("global::System.Text.Json.Nodes.JsonNode"), "node", "this")
            .Body(Cs.Tree(Return(NodeCall(codec + ".NodeGetValue_" + index, Cs.Symbol("node"))))).End();
    }

    private static string RuntimeMethod(IMethodSymbol method)
    {
        string first = method.Parameters[0].Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();
        bool stream = first is "System.IO.Stream" or "System.IO.Pipelines.PipeReader" or "System.IO.Pipelines.PipeWriter";
        return method.Name switch
        {
            "SerializeAsync" => "EncodeRuntimeStreamAsync",
            "DeserializeAsync" => "DecodeRuntimeStreamAsync",
            "Serialize" when stream => "EncodeRuntimeStream",
            "Serialize" when first == "System.Text.Json.Utf8JsonWriter" => "EncodeRuntimeWriter",
            "Serialize" => "EncodeRuntime",
            "SerializeToUtf8Bytes" => "EncodeRuntimeBytes",
            "SerializeToElement" => "EncodeRuntimeElement",
            "SerializeToDocument" => "EncodeRuntimeDocument",
            "SerializeToNode" => "EncodeRuntimeNode",
            "Deserialize" when stream => "DecodeRuntimeStream",
            "Deserialize" when first == "System.Text.Json.Utf8JsonReader" => "DecodeRuntimeReader",
            "Deserialize" when first == "System.Text.Json.JsonElement" => "DecodeRuntimeElement",
            "Deserialize" when first == "System.Text.Json.JsonDocument" => "DecodeRuntimeDocument",
            "Deserialize" when first == "System.Text.Json.Nodes.JsonNode" => "DecodeRuntimeNode",
            "Deserialize" when first == "System.ReadOnlySpan<byte>" => "DecodeRuntimeBytes",
            "Deserialize" when first == "System.ReadOnlySpan<char>" => "DecodeRuntimeChars",
            "Deserialize" => "DecodeRuntime",
            _ => throw new InvalidOperationException("Unsupported runtime JSON operation: " + method.Name)
        };
    }

}
