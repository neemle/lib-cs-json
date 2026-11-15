using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class CompilerJsonGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(context.CompilationProvider, Generate);

    private static void Generate(SourceProductionContext output, Compilation compilation)
    {
        var emitter = new CodecEmitter(compilation.GetSpecialType(SpecialType.System_Object));
        var contexts = new AsyncContextEmitter();
        var document = Cs.Document(CsLanguageVersion.CSharp14).Namespace("Neemle.Json.Interceptors")
            .Nullable(NullableMode.Enable);
        var interceptors = document.Class("GeneratedInterceptors").Internal().Static();
        string assembly = compilation.AssemblyName ?? "";
        bool dynamicValues = false;
        int count = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot(output.CancellationToken).DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "GetValue", TypeArguments.Length: 1 } nodeMethod &&
                    nodeMethod.ContainingType.ToDisplayString() == "System.Text.Json.Nodes.JsonNode" &&
                    emitter.TryRegister(nodeMethod.TypeArguments[0], out int nodeIndex) &&
                    model.GetInterceptableLocation(invocation, output.CancellationToken) is { } nodeLocation)
                {
                    emitter.RegisterNodeValue(nodeIndex);
                    AddNodeInterceptor(interceptors, nodeMethod, nodeLocation, assembly, nodeIndex, count++);
                    continue;
                }
                if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add",
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TypeClassifiers" } classifiers } &&
                    model.GetSymbolInfo(classifiers).Symbol is IPropertySymbol property &&
                    property.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializerOptions")
                    contexts.RegisterOptionsClassifier();
                if (!CompilerJsonCall.TryCreate(model, invocation, out var call)) continue;
                dynamicValues |= call.Type.SpecialType == SpecialType.System_Object;
                int index = -1;
                bool generic = call.Type is ITypeParameterSymbol;
                if (generic) emitter.TryRegister(compilation.GetSpecialType(SpecialType.System_Object), out _);
                bool framework = !generic && (call.Operation == "Framework" || !emitter.TryRegister(call.Type, out index));
                if (framework && !contexts.RegisterUnsupported(call.Type)) continue;
                if (!framework && call.Method.Name == "DeserializeAsyncEnumerable") emitter.RegisterAsyncSequence(index);
                var location = model.GetInterceptableLocation(invocation, output.CancellationToken);
                if (location is null) continue;
                AddInterceptor(interceptors, call, location, assembly, index, count++, framework, generic);
            }
        }
        if (count == 0) return;
        if (dynamicValues)
        {
            var candidates = new List<ITypeSymbol>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var expression in tree.GetRoot(output.CancellationToken).DescendantNodes().OfType<ExpressionSyntax>())
                {
                    var info = model.GetTypeInfo(expression, output.CancellationToken);
                    if (info.ConvertedType?.SpecialType == SpecialType.System_Object && info.Type is { } type &&
                        type.SpecialType != SpecialType.System_Object && MetadataTypes.CandidateVisible(type, compilation.Assembly))
                        candidates.Add(type);
                    if (expression is TypeOfExpressionSyntax typeOf && model.GetTypeInfo(typeOf.Type).Type is { } known &&
                        MetadataTypes.CandidateVisible(known, compilation.Assembly)) candidates.Add(known);
                }
            }
            emitter.SetDynamicCandidates(candidates);
        }
        interceptors.End();
        var options = new RenderOptions { NamespaceStyle = NamespaceStyle.BlockScoped };
        var attribute = CSharpSyntaxTree.ParseText(AttributeDocument().Render(options)).GetCompilationUnitRoot();
        var methods = CSharpSyntaxTree.ParseText(document.Render(options)).GetCompilationUnitRoot();
        output.AddSource("NeemleInterceptors.g.cs", attribute.AddMembers(methods.Members.ToArray()).ToFullString());
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? new();
        if (contexts.Generate() is { } contextSource)
            GenerateFrameworkContext(output, compilation, parseOptions, contextSource);
        var sources = GeneratedCodeLayout.Split(emitter.Generate());
        for (int i = 0; i < sources.Count; i++)
            output.AddSource($"NeemleCodecs{i}.g.cs", GeneratedAssemblyNames.Rename(sources[i], assembly, parseOptions));
        if (parseOptions.PreprocessorSymbolNames.Contains("NEEMLE_STATIC_NATIVE_METADATA", StringComparer.Ordinal))
        {
            var declared = compilation.SyntaxTrees.SelectMany(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                return tree.GetRoot(output.CancellationToken).DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Select(node => model.GetDeclaredSymbol(node)).OfType<ITypeSymbol>();
            });
            var types = MetadataTypes.CollectMetadataTypes(declared.Concat(emitter.RegisteredTypes).Concat(contexts.KnownTypes), compilation.Assembly);
            var metadata = NativeTypeMetadataEmitter.Generate(types);
            for (int i = 0; i < metadata.Count; i++) output.AddSource($"NeemleMetadata{i}.g.cs", metadata[i]);
        }
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

    private static void AddInterceptor(Neemle.CSharpComposer.Builders.TypeBuilder<Neemle.CSharpComposer.Builders.FileBuilder> root,
        CompilerJsonCall call, InterceptableLocation location, string assembly, int index, int position, bool framework, bool generic)
    {
        var format = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        string result = call.Method.ReturnType.ToDisplayString(format);
        string codec = GeneratedAssemblyNames.Name("GeneratedJsonCodec_" + index, assembly);
        var method = root.Method(NodeType(result), "Call" + position).Internal().Static()
            .Attribute(Cs.Attribute(Cs.Type("System.Runtime.CompilerServices.InterceptsLocation"),
                Cs.Arg(Cs.Literal(location.Version)), Cs.Arg(Cs.Literal(location.Data))));
        if (generic) method.Generic(call.Type.Name);
        var arguments = new List<CsNode>();
        for (int i = 0; i < call.Method.Parameters.Length; i++)
        {
            var parameter = call.Method.Parameters[i];
            string name = "arg" + i;
            string? modifier = parameter.RefKind == RefKind.Ref ? "ref" : parameter.RefKind == RefKind.In ? "in" : null;
            method.Parameter(NodeType(parameter.Type.ToDisplayString(format)), name, modifier);
            CsNode value = Cs.Symbol(name);
            var type = parameter.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();
            if (framework && type == "System.Type") continue;
            if (framework && type == "System.Text.Json.JsonSerializerOptions")
            {
                var info = NodeCall("GeneratedAsyncJsonContext.ForOptions", Cs.Symbol(name));
                var lookup = Cs.Symbol("GetTypeInfo", new() { Target = info, AsCall = true,
                    Arguments = Cs.Compound(TypeOf(TypeName(call.Type))) });
                string metadata = call.Method.IsGenericMethod ?
                    "System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + TypeName(call.Type) + ">" :
                    "System.Text.Json.Serialization.Metadata.JsonTypeInfo";
                value = Cast(metadata, NonNull(lookup));
            }
            if (!framework && !generic && type is
                ("System.IO.Pipelines.PipeReader" or "System.IO.Pipelines.PipeWriter"))
                value = NodeCall(name + ".AsStream", Cs.Value(true));
            if (parameter.RefKind == RefKind.Ref) value = Arg(ArgumentModifier.Ref, value);
            arguments.Add(value);
        }
        if (framework && call.Method.Name is ("SerializeAsyncEnumerable" or "DeserializeAsyncEnumerable"))
        {
            int optionsIndex = call.Method.Parameters.IndexOf(call.Method.Parameters.First(parameter =>
                parameter.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == "System.Text.Json.JsonSerializerOptions"));
            int boolIndex = -1;
            for (int i = 0; i < call.Method.Parameters.Length; i++)
                if (call.Method.Parameters[i].Type.SpecialType == SpecialType.System_Boolean) boolIndex = i;
            if (boolIndex >= 0 && boolIndex < optionsIndex)
                (arguments[boolIndex], arguments[optionsIndex]) = (arguments[optionsIndex], arguments[boolIndex]);
        }
        if (generic) arguments.Insert(call.Method.Name == "Deserialize" ? 1 : 2, TypeOf(call.Type.Name));
        var invoke = NodeCall(call.RuntimeType ? GeneratedAssemblyNames.Name("GeneratedJson", assembly) + "." + RuntimeMethod(call.Method) :
            generic ? GeneratedAssemblyNames.Name("GeneratedJson", assembly) +
            (call.Method.Name == "Deserialize" ? ".DecodeRuntimeReader" : ".EncodeRuntimeWriter") :
            framework ? "System.Text.Json.JsonSerializer." + call.Method.Name :
            codec + "." + call.Operation + "_" + index, arguments.ToArray());
        if (generic && call.Method.Name == "Deserialize")
        {
            method.Body(Cs.Tree(Return(Cast(call.Type.Name, NonNull(invoke))))).End();
            return;
        }
        if (!framework && call.Method.Name == "DeserializeAsync" && call.Type.IsReferenceType)
        {
            method.Async().Body(Cs.Tree(Return(invoke with { Options = invoke.Options with { Await = true } }))).End();
            return;
        }
        method.Body(call.Method.ReturnsVoid ? Cs.Tree(invoke) : Cs.Tree(Return(invoke))).End();
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

    private static void GenerateFrameworkContext(SourceProductionContext output, Compilation compilation,
        CSharpParseOptions options, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, options, "NeemleFrameworkContext.cs", System.Text.Encoding.UTF8);
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item =>
            item.GetName().Name == "System.Text.Json.SourceGeneration") ??
            throw new InvalidOperationException("The SDK JSON source generator is required.");
        var generator = (IIncrementalGenerator)(Activator.CreateInstance(
            assembly.GetType("System.Text.Json.SourceGeneration.JsonSourceGenerator", true)!) ??
            throw new InvalidOperationException("Cannot create the SDK JSON source generator."));
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: options)
            .RunGenerators(compilation.AddSyntaxTrees(tree), output.CancellationToken);
        var result = driver.GetRunResult();
        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Location.SourceTree != tree) output.ReportDiagnostic(diagnostic);
            else if (diagnostic.Severity >= DiagnosticSeverity.Warning)
                throw new InvalidOperationException(diagnostic.ToString());
        }
        var generated = result.Results.SelectMany(item => item.GeneratedSources)
            .Where(item => item.HintName.StartsWith("GeneratedAsyncJsonContext", StringComparison.Ordinal)).ToArray();
        if (generated.Length == 0) throw new InvalidOperationException("The SDK did not generate JSON metadata.");
        output.AddSource("NeemleFrameworkContext.g.cs", source);
        foreach (var item in generated) output.AddSource("NeemleFramework." + item.HintName, item.SourceText);
    }

    private static Neemle.CSharpComposer.Builders.FileBuilder AttributeDocument()
    {
        var document = Cs.Document(CsLanguageVersion.CSharp14).Namespace("System.Runtime.CompilerServices")
            .Nullable(NullableMode.Enable);
        var attribute = document.Class("InterceptsLocationAttribute").Modifier("file").Sealed().Base(Cs.Type("System.Attribute"));
        var targets = Cs.Member(Cs.Member(Cs.Id("System"), "AttributeTargets"), "Method");
        attribute.Attribute(Cs.Attribute(Cs.Type("System.AttributeUsage"), Cs.Arg(targets),
            new AttributeArgument(Cs.Literal(true), "AllowMultiple", false)));
        attribute.Constructor().Public().Parameter(Cs.Int, "version").Parameter(Cs.Type("string"), "data").End();
        attribute.End();
        return document;
    }
}
