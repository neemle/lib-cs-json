using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

[Generator(LanguageNames.CSharp)]
public sealed partial class CompilerJsonGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor OpenGeneric = new("NEEMLE001", "Unsupported generic JSON call",
        "JSON call for open type parameter '{0}' needs a JsonTypeInfo<T> or a concrete type", "Neemle.JSON",
        DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(context.CompilationProvider, Generate);

    private static void Generate(SourceProductionContext output, Compilation compilation)
    {
        var emitter = new CodecEmitter(compilation.GetSpecialType(SpecialType.System_Object));
        string assembly = compilation.AssemblyName ?? "";
        string contextName = GeneratedAssemblyNames.Name("GeneratedAsyncJsonContext", assembly);
        var contexts = new AsyncContextEmitter(contextName);
        bool hasHttp = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Builder.WebApplication") is not null;
        foreach (var type in HttpJsonRoots.Find(compilation, output.CancellationToken)) contexts.RegisterUnsupported(type);
        var document = Cs.Document(CsLanguageVersion.CSharp14).Namespace("Neemle.Json.Interceptors")
            .Nullable(NullableMode.Enable);
        var interceptors = document.Class("GeneratedInterceptors").Internal().Static();
        bool dynamicValues = false;
        bool usesEnumConverter = false;
        var interceptedTypes = new List<ITypeSymbol>();
        var contextRoots = new List<ITypeSymbol>();
        int count = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot(output.CancellationToken).DescendantNodes().OfType<TypeDeclarationSyntax>())
                if (model.GetDeclaredSymbol(declaration, output.CancellationToken) is { } declared)
                {
                    if (MetadataTypes.CandidateVisible(declared, compilation.Assembly) && declared.GetAttributes().Any(item =>
                            CodecEmitter.IsJsonAttribute(item, "JsonPolymorphicAttribute") ||
                            CodecEmitter.IsJsonAttribute(item, "JsonDerivedTypeAttribute") ||
                            CodecEmitter.IsJsonAttribute(item, "JsonConverterAttribute"))) contextRoots.Add(declared);
                    foreach (var contextAttribute in declared.GetAttributes().Where(item =>
                                 CodecEmitter.IsJsonAttribute(item, "JsonSerializableAttribute")))
                        if (contextAttribute.ConstructorArguments.Length != 0 &&
                            contextAttribute.ConstructorArguments[0].Kind != TypedConstantKind.Array &&
                            contextAttribute.ConstructorArguments[0].Value is ITypeSymbol root) contextRoots.Add(root);
                }
            usesEnumConverter |= tree.GetRoot(output.CancellationToken).DescendantNodes().Any(node =>
            {
                var type = node switch
                {
                    BaseObjectCreationExpressionSyntax creation => model.GetTypeInfo(creation).Type,
                    TypeOfExpressionSyntax typeOf => model.GetTypeInfo(typeOf.Type).Type,
                    _ => null
                };
                return type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() is
                    "System.Text.Json.Serialization.JsonStringEnumConverter" or
                    "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";
            });
            foreach (var invocation in tree.GetRoot(output.CancellationToken).DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (HttpJsonCall.TryCreate(model, invocation, out var httpMethod) &&
                    contexts.RegisterUnsupported(httpMethod.TypeArguments[0]) &&
                    model.GetInterceptableLocation(invocation, output.CancellationToken) is { } httpLocation)
                {
                    interceptedTypes.Add(httpMethod.TypeArguments[0]);
                    AddHttpJsonInterceptor(interceptors, httpMethod, httpLocation, assembly, count++);
                    continue;
                }
                if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "GetValue", TypeArguments.Length: 1 } nodeMethod &&
                    nodeMethod.ContainingType.ToDisplayString() == "System.Text.Json.Nodes.JsonNode" &&
                    emitter.TryRegister(nodeMethod.TypeArguments[0], out int nodeIndex) &&
                    model.GetInterceptableLocation(invocation, output.CancellationToken) is { } nodeLocation)
                {
                    emitter.RegisterNodeValue(nodeIndex);
                    AddNodeInterceptor(interceptors, nodeMethod, nodeLocation, assembly, nodeIndex, count++);
                    continue;
                }
                if (invocation.Expression is MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "Add",
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TypeClassifiers" } classifiers
                    } &&
                    model.GetSymbolInfo(classifiers).Symbol is IPropertySymbol property &&
                    property.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializerOptions")
                    contexts.RegisterOptionsClassifier();
                if (!CompilerJsonCall.TryCreate(model, invocation, out var call))
                {
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method &&
                        method.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializer" &&
                        method.TypeArguments.FirstOrDefault() is ITypeParameterSymbol parameter &&
                        method.Parameters.Any(item => item.Type.WithNullableAnnotation(NullableAnnotation.None)
                            .ToDisplayString() == "System.Text.Json.JsonSerializerOptions"))
                        output.ReportDiagnostic(Diagnostic.Create(OpenGeneric, invocation.GetLocation(), parameter.Name));
                    continue;
                }
                if (call.Type is ITypeParameterSymbol genericParameter && !IsGenericConverter(genericParameter))
                {
                    output.ReportDiagnostic(Diagnostic.Create(OpenGeneric, invocation.GetLocation(), genericParameter.Name));
                    continue;
                }
                if (call.Type is not ITypeParameterSymbol) interceptedTypes.Add(call.Type);
                dynamicValues |= call.Type.SpecialType == SpecialType.System_Object;
                int index = -1;
                bool generic = call.Type is ITypeParameterSymbol;
                if (generic) emitter.TryRegister(compilation.GetSpecialType(SpecialType.System_Object), out _);
                bool framework = !generic && !call.RuntimeType && (call.Operation == "Framework" ||
                    UsesRegisteredConverters(model, invocation) || !emitter.TryRegister(call.Type, out index));
                if (call.RuntimeType) emitter.TryRegister(call.Type, out index);
                if (call.Type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum) emitter.TryRegister(call.Type, out index);
                if (framework && !contexts.RegisterUnsupported(call.Type)) continue;
                if (!framework && call.Method.Name == "DeserializeAsyncEnumerable") emitter.RegisterAsyncSequence(index);
                var location = model.GetInterceptableLocation(invocation, output.CancellationToken);
                if (location is null) continue;
                AddInterceptor(interceptors, call, location, assembly, index, count++, framework, generic);
            }
        }
        if (usesEnumConverter)
        {
            var candidates = compilation.SyntaxTrees.SelectMany(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                return tree.GetRoot(output.CancellationToken).DescendantNodes().Select(node => node switch
                {
                    EnumDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration) as ITypeSymbol,
                    TypeOfExpressionSyntax typeOf => model.GetTypeInfo(typeOf.Type).Type,
                    _ => null
                }).OfType<ITypeSymbol>();
            });
            foreach (var type in MetadataTypes.CollectMetadataTypes(candidates.Where(type =>
                         type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum).Concat(contextRoots).Concat(interceptedTypes), compilation.Assembly))
                if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum) emitter.TryRegister(type, out _);
        }
        if (count == 0 && !usesEnumConverter && !hasHttp && contextRoots.Count == 0) return;
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
        string interceptorSource = attribute.AddMembers(methods.Members.ToArray()).ToFullString();
        output.AddSource("NeemleInterceptors.g.cs", interceptorSource.Replace("#nullable enable",
            "#nullable enable\n#nullable disable warnings", StringComparison.Ordinal));
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? new();
        string? contextSource = contexts.Generate();
        if (contextSource is not null)
            GenerateFrameworkContext(output, compilation, parseOptions, contextSource, contextName);
        if (hasHttp || contextSource is not null) output.AddSource("NeemleHttpResolver.g.cs", HttpJsonResolverEmitter.Generate(
            contextSource is null ? null : contextName, hasHttp && compilation.Options.OutputKind is
                (OutputKind.ConsoleApplication or OutputKind.WindowsApplication)));
        var sources = GeneratedCodeLayout.Split(emitter.Generate());
        for (int i = 0; i < sources.Count; i++)
            output.AddSource($"NeemleCodecs{i}.g.cs", GeneratedAssemblyNames.Rename(sources[i], assembly, parseOptions));
        var types = MetadataTypes.CollectMetadataTypes(contextRoots.Concat(interceptedTypes).Concat(emitter.RegisteredTypes)
            .Concat(contexts.KnownTypes), compilation.Assembly).ToArray();
        if (types.Length != 0)
        {
            var metadata = NativeTypeMetadataEmitter.Generate(types);
            for (int i = 0; i < metadata.Count; i++) output.AddSource($"NeemleMetadata{i}.g.cs", metadata[i]);
            if (EnumConverterRegistryEmitter.Generate(emitter.RegisteredTypes.ToArray(), assembly) is { } enumConverters)
                output.AddSource("NeemleEnumConverters.g.cs", enumConverters);
        }
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
                var info = NodeCall(GeneratedAssemblyNames.Name("GeneratedAsyncJsonContext", assembly) + ".ForOptions", Cs.Symbol(name));
                var lookup = Cs.Symbol("GetTypeInfo", new()
                {
                    Target = info,
                    AsCall = true,
                    Arguments = Cs.Compound(TypeOf(TypeName(call.Type)))
                });
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

    private static void GenerateFrameworkContext(SourceProductionContext output, Compilation compilation,
        CSharpParseOptions options, string source, string contextName)
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
            .Where(item => item.HintName.StartsWith(contextName, StringComparison.Ordinal)).ToArray();
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
