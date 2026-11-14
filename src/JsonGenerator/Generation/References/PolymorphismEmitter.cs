using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class PolymorphismEmitter
{
    internal sealed record Variant(INamedTypeSymbol Type, object? Discriminator);

    internal static bool HasContract(INamedTypeSymbol type) => type.GetAttributes().Any(attribute =>
        CodecEmitter.IsJsonAttribute(attribute, "JsonDerivedTypeAttribute") ||
        CodecEmitter.IsJsonAttribute(attribute, "JsonPolymorphicAttribute"));

    internal static Variant[]? Variants(INamedTypeSymbol type)
    {
        var attributes = type.GetAttributes();
        var settings = attributes.FirstOrDefault(attribute =>
            CodecEmitter.IsJsonAttribute(attribute, "JsonPolymorphicAttribute"));
        if (settings is not null && settings.NamedArguments.Any(argument => argument.Key is not
            ("TypeDiscriminatorPropertyName" or "IgnoreUnrecognizedTypeDiscriminators" or "UnknownDerivedTypeHandling")))
            return null;
        var result = new List<Variant>();
        foreach (var attribute in attributes.Where(attribute =>
            CodecEmitter.IsJsonAttribute(attribute, "JsonDerivedTypeAttribute")))
        {
            if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not
                INamedTypeSymbol derived || derived.IsAbstract || derived.TypeKind != Microsoft.CodeAnalysis.TypeKind.Class ||
                !Inherits(derived, type)) return null;
            object? discriminator = attribute.ConstructorArguments.Length > 1 ?
                attribute.ConstructorArguments[1].Value : null;
            if (discriminator is not (null or string or int)) return null;
            if (result.Any(item => SymbolEqualityComparer.Default.Equals(item.Type, derived) ||
                discriminator is not null && Equals(item.Discriminator, discriminator))) return null;
            result.Add(new Variant(derived, discriminator));
        }
        return result.Count == 0 ? null : result.OrderByDescending(item => Depth(item.Type)).ToArray();
    }

    private static int Depth(INamedTypeSymbol type)
    {
        int result = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType) result++;
        return result;
    }

    private static object? Setting(INamedTypeSymbol type, string name) => type.GetAttributes().FirstOrDefault(attribute =>
        CodecEmitter.IsJsonAttribute(attribute, "JsonPolymorphicAttribute"))?.NamedArguments
        .FirstOrDefault(argument => argument.Key == name).Value.Value;

    private static bool Inherits(INamedTypeSymbol derived, INamedTypeSymbol parent)
    {
        if (derived.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, parent))) return true;
        for (var current = derived.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, parent)) return true;
        return false;
    }

    private static string MetadataName(INamedTypeSymbol type) => type.GetAttributes().FirstOrDefault(attribute =>
        CodecEmitter.IsJsonAttribute(attribute, "JsonPolymorphicAttribute"))?.NamedArguments
        .FirstOrDefault(argument => argument.Key == "TypeDiscriminatorPropertyName").Value.Value as string ?? "$type";

    internal static void Emit(TypeBuilder<FileBuilder> root, CodecEmitter owner, INamedTypeSymbol type, int index)
    {
        var variants = Variants(type)!;
        string name = TypeName(type);
        string metadata = MetadataName(type);
        var writers = new List<string>();
        var readers = new List<string>();
        foreach (var variant in variants)
        {
            int derived = owner.Register(variant.Type);
            string suffix = $"{index}_{writers.Count}";
            writers.Add(EmitWriter(root, owner, variant, (suffix, derived, metadata)));
            if (variant.Discriminator is not null)
                readers.Add(EmitReader(root, variant, (suffix, derived, name, metadata)));
        }
        var known = owner.KnownTypes.OfType<INamedTypeSymbol>().Where(candidate => !candidate.IsAbstract &&
            Inherits(candidate, type) && !variants.Any(variant => SymbolEqualityComparer.Default.Equals(
                variant.Type, candidate))).Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>().ToArray();
        EmitUnknownName(root, index, known);
        EmitWriterDispatch(root, type, index, variants, writers);
        EmitReaderDispatch(root, type, index, metadata, readers);
    }

    private static string EmitWriter(TypeBuilder<FileBuilder> root, CodecEmitter owner, Variant variant,
        (string Suffix, int Index, string Metadata) context)
    {
        string method = "WritePolymorphic_" + context.Suffix;
        var body = new List<CsNode>();
        if (variant.Discriminator is null)
        {
            body.Add(NodeIf(NodeCall($"TryWriteCustom_{context.Index}", Cs.Symbol("w"), Cs.Symbol("value"),
                Cs.Symbol("options"), Cs.Symbol("path")), Cs.Instruction(InstructionKind.Return)));
            body.Add(NodeCall($"WriteCore_{context.Index}", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"),
                Cs.Symbol("path")));
        }
        else
        {
            var registered = NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.AndAlso,
                NullTest(Cs.Symbol("options"), true), NodeCall($"Table_{context.Index}.TryGetValue",
                    Cs.Symbol("options"), OutVar("custom"))), NullTest(NodeMember(Cs.Symbol("custom"), "Converter"), true));
            body.Add(NodeIf(registered, NodeThrow(NodeNew("NotSupportedException"))));
            body.Add(NodeCall("CheckWriteDepth", Cs.Symbol("w"), Cs.Symbol("options"), Cs.Symbol("path")));
            if (CodecEmitter.OptionalFields(variant.Type).Any(field => !owner.SupportsOptionalField(field)))
                body.Add(NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("IncludeFields", new()
                { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(true)),
                    NodeThrow(NodeNew("NotSupportedException"))));
            body.Add(NodeCall($"ValidateNames_{context.Index}", Cs.Symbol("options")));
            ObjectCodecEmitter.EmitCallback(body, variant.Type, "IJsonOnSerializing", "OnSerializing", "value");
            body.Add(NodeCall("w.WriteStartObject"));
            body.Add(NodeCall("WriteReferenceId", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options")));
            body.Add(NodeCall(variant.Discriminator is int ? "w.WriteNumber" : "w.WriteString", Cs.Value(context.Metadata),
                Cs.Value(variant.Discriminator)));
            body.Add(NodeCall($"WriteMembers_{context.Index}", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"),
                Cs.Symbol("path")));
            if (CodecEmitter.ExtensionMember(variant.Type) is { } extension)
                body.Add(NodeIf(ObjectCodecEmitter.ExtensionEnabled(extension), NodeCall($"WriteExtra_{context.Index}",
                    Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Symbol("path"))));
            body.Add(NodeCall("w.WriteEndObject"));
            ObjectCodecEmitter.EmitCallback(body, variant.Type, "IJsonOnSerialized", "OnSerialized", "value");
        }
        root.Method(Cs.Void, method).Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "w")
            .Parameter(NodeType(TypeName(variant.Type)), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
        return method;
    }

    private static string EmitReader(TypeBuilder<FileBuilder> root, Variant variant,
        (string Suffix, int Index, string BaseType, string Metadata) context)
    {
        string method = "ReadPolymorphic_" + context.Suffix;
        string kind = variant.Discriminator is int ? "Number" : "String";
        var sameKind = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("tag"), "ValueKind"),
            NodeMember(Cs.Symbol("JsonValueKind"), kind));
        CsNode matches = variant.Discriminator is int ? NodeBinary(CsOperator.AndAlso, sameKind,
            NodeBinary(CsOperator.AndAlso, NodeCall("tag.TryGetInt32", OutVar("number")),
                NodeBinary(CsOperator.Equal, Cs.Symbol("number"), Cs.Value(variant.Discriminator)))) :
            NodeBinary(CsOperator.AndAlso, sameKind,
                NodeBinary(CsOperator.Equal, NodeCall("tag.GetString"), Cs.Value(variant.Discriminator)));
        var body = new List<CsNode> { NodeAssign(Cs.Symbol("result"), NonNull(DefaultValue())) };
        body.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { matches } }),
            Return(Cs.Value(false))));
        body.Add(Cs.Symbol("document", new()
        {
            AsVar = true,
            Using = true,
            Value = NodeCall("WithoutMetadata", Cs.Symbol("e"), Cs.Value(context.Metadata), Cs.Symbol("options"))
        }));
        body.Add(NodeAssign(Cs.Symbol("result"), NodeCall($"Read_{context.Index}", NodeMember(Cs.Symbol("document"),
            "RootElement"), Cs.Symbol("options"), Cs.Symbol("path"))));
        body.Add(Return(Cs.Value(true)));
        root.Method(Cs.Bool, method).Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonElement"), "tag").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("string"), "path").Parameter(NodeType(context.BaseType), "result", "out")
            .Body(Cs.Tree(body.ToArray())).End();
        return method;
    }

    private static void EmitWriterDispatch(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index, Variant[] variants,
        List<string> methods)
    {
        var batches = new List<string>();
        for (int offset = 0; offset < variants.Length; offset += 3)
        {
            string batch = $"TryWritePolyBatch_{index}_0_{batches.Count}";
            batches.Add(batch);
            var cases = new List<CsNode>();
            for (int i = offset; i < Math.Min(offset + 3, variants.Length); i++)
            {
                string local = "derived_" + i;
                var test = Cs.Symbol(new()
                {
                    AsIs = true,
                    Value = Cs.Symbol("value"),
                    Pattern = new CsPattern(PatternKind.Declaration)
                    { Type = NodeType(TypeName(variants[i].Type)), Name = local }
                });
                var exact = NodeBinary(CsOperator.Equal, NodeCall(local + ".GetType"), TypeOf(TypeName(variants[i].Type)));
                CsNode matches = (int?)Setting(type, "UnknownDerivedTypeHandling") == 2 ? test :
                    NodeBinary(CsOperator.AndAlso, test, exact);
                cases.Add(NodeIf(matches, NodeCall(methods[i], Cs.Symbol("w"), Cs.Symbol(local),
                    Cs.Symbol("options"), Cs.Symbol("path")), Return(Cs.Value(true))));
            }
            cases.Add(Return(Cs.Value(false)));
            WriterBatch(root, batch, type).Body(Cs.Tree(cases.ToArray())).End();
        }
        batches = CollapseWriterBatches(root, type, index, batches);
        var body = batches.Select(batch => (CsNode)NodeIf(NodeCall(batch, Cs.Symbol("w"), Cs.Symbol("value"),
            Cs.Symbol("options"), Cs.Symbol("path")), Return(Cs.Value(true)))).ToList();
        if ((int?)Setting(type, "UnknownDerivedTypeHandling") is null or 0)
            body.Add(NodeIf(NodeBinary(CsOperator.NotEqual, NodeCall("value.GetType"), TypeOf(TypeName(type))),
                NodeThrow(NodeNew("NotSupportedException", NodeCall($"UnknownDerivedName_{index}",
                    NodeCall("value.GetType"))))));
        body.Add(Return(Cs.Value(false)));
        root.Method(Cs.Bool, $"TryWritePolymorphic_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(TypeName(type)), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitUnknownName(TypeBuilder<FileBuilder> root, int index, INamedTypeSymbol[] known)
    {
        var methods = new List<string>();
        foreach (var group in known.Chunk(3))
        {
            string name = $"UnknownDerivedBatch_{index}_{methods.Count}";
            methods.Add(name);
            var body = group.Select(type => (CsNode)NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("type"),
                TypeOf(TypeName(type))), Return(Cs.Value(type.ToDisplayString())))).ToList();
            body.Add(Return(Cs.Value(null)));
            root.Method(Cs.Type("string").Nullable(), name).Private().Static().Parameter(Cs.Type("Type"), "type")
                .Body(Cs.Tree(body.ToArray())).End();
        }
        var result = new List<CsNode>();
        for (int i = 0; i < methods.Count; i++)
        {
            string variable = "name" + i;
            result.Add(NodeVar(variable, NodeCall(methods[i], Cs.Symbol("type"))));
            result.Add(NodeIf(NullTest(Cs.Symbol(variable), true), Return(Cs.Symbol(variable))));
        }
        result.Add(Return(Cs.Value("Unknown derived JSON type.")));
        root.Method(Cs.Type("string"), $"UnknownDerivedName_{index}").Private().Static()
            .Parameter(Cs.Type("Type"), "type").Body(Cs.Tree(result.ToArray())).End();
    }

    private static List<string> CollapseWriterBatches(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index,
        List<string> batches)
    {
        int level = 1;
        while (batches.Count > 3)
        {
            var next = new List<string>();
            foreach (var group in batches.Chunk(3))
            {
                string batch = $"TryWritePolyBatch_{index}_{level}_{next.Count}";
                next.Add(batch);
                var body = group.Select(item => (CsNode)NodeIf(NodeCall(item, Cs.Symbol("w"), Cs.Symbol("value"),
                    Cs.Symbol("options"), Cs.Symbol("path")), Return(Cs.Value(true)))).ToList();
                body.Add(Return(Cs.Value(false)));
                WriterBatch(root, batch, type).Body(Cs.Tree(body.ToArray())).End();
            }
            batches = next;
            level++;
        }
        return batches;
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> WriterBatch(TypeBuilder<FileBuilder> root, string name,
        INamedTypeSymbol type) => root.Method(Cs.Bool, name).Private().Static()
        .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(TypeName(type)), "value")
        .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path");

    private static void EmitReaderDispatch(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index, string metadata,
        List<string> methods)
    {
        methods = CollapseReaderBatches(root, type, index, methods);
        var body = new List<CsNode> { NodeAssign(Cs.Symbol("result"), NonNull(DefaultValue())) };
        if (methods.Count == 0) body.Add(Return(Cs.Value(false)));
        else
        {
            CsNode present = NodeCall("e.TryGetProperty", Cs.Value(metadata), OutVar("tag"));
            var objectKind = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"),
                NodeMember(Cs.Symbol("JsonValueKind"), "Object"));
            var absent = Cs.Symbol(new()
            {
                Operator = CsOperator.Not,
                Parts = new CsNode[] { NodeBinary(CsOperator.AndAlso, objectKind, present) }
            });
            body.Add(NodeIf(absent, Return(Cs.Value(false))));
            body.Add(NodeCall("RejectDuplicateMetadata", Cs.Symbol("e"), Cs.Value(metadata)));
            body.Add(NodeCall("RequireFirstMetadata", Cs.Symbol("e"), Cs.Value(metadata), Cs.Symbol("options")));
            foreach (string method in methods) body.Add(NodeIf(NodeCall(method, Cs.Symbol("e"), Cs.Symbol("tag"),
                Cs.Symbol("options"), Cs.Symbol("path"), Arg(ArgumentModifier.Out, Cs.Symbol("result"))),
                Return(Cs.Value(true))));
            if ((bool?)Setting(type, "IgnoreUnrecognizedTypeDiscriminators") == true)
            {
                body.Add(Cs.Symbol("document", new()
                {
                    AsVar = true,
                    Using = true,
                    Value = NodeCall("WithoutMetadata", Cs.Symbol("e"), Cs.Value(metadata), Cs.Symbol("options"))
                }));
                body.Add(NodeAssign(Cs.Symbol("result"), NodeCall($"Read_{index}",
                    NodeMember(Cs.Symbol("document"), "RootElement"), Cs.Symbol("options"), Cs.Symbol("path"))));
                body.Add(Return(Cs.Value(true)));
            }
            else body.Add(NodeThrow(NodeNew("JsonException")));
        }
        root.Method(Cs.Bool, $"TryReadPolymorphic_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("string"), "path").Parameter(NodeType(TypeName(type)), "result", "out")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static List<string> CollapseReaderBatches(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index,
        List<string> methods)
    {
        int level = 0;
        while (methods.Count > 3)
        {
            var next = new List<string>();
            foreach (var group in methods.Chunk(3))
            {
                string batch = $"TryReadPolyBatch_{index}_{level}_{next.Count}";
                next.Add(batch);
                var body = new List<CsNode> { NodeAssign(Cs.Symbol("result"), NonNull(DefaultValue())) };
                body.AddRange(group.Select(item => (CsNode)NodeIf(NodeCall(item, Cs.Symbol("e"), Cs.Symbol("tag"),
                    Cs.Symbol("options"), Cs.Symbol("path"), Arg(ArgumentModifier.Out, Cs.Symbol("result"))),
                    Return(Cs.Value(true)))));
                body.Add(Return(Cs.Value(false)));
                root.Method(Cs.Bool, batch).Private().Static().Parameter(Cs.Type("JsonElement"), "e")
                    .Parameter(Cs.Type("JsonElement"), "tag")
                    .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
                    .Parameter(Cs.Type("string"), "path").Parameter(NodeType(TypeName(type)), "result", "out")
                    .Body(Cs.Tree(body.ToArray())).End();
            }
            methods = next;
            level++;
        }
        return methods;
    }

    internal static void EmitCommon(TypeBuilder<FileBuilder> root)
    {
        var check = NodeIf(NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("property"), "Name"), Cs.Symbol("name")),
            NodeIf(Cs.Symbol("seen"), NodeThrow(NodeNew("JsonException"))), NodeAssign(Cs.Symbol("seen"), Cs.Value(true)));
        root.Method(Cs.Void, "RejectDuplicateMetadata").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("string"), "name").Body(Cs.Tree(NodeVar("seen", Cs.Value(false)),
                NodeEach("property", NodeCall("e.EnumerateObject"), check))).End();
        var write = NodeEach("property", NodeCall("e.EnumerateObject"),
            NodeIf(NodeBinary(CsOperator.NotEqual, NodeMember(Cs.Symbol("property"), "Name"), Cs.Symbol("name")),
                NodeCall("property.WriteTo", Cs.Symbol("writer"))));
        root.Method(Cs.Type("JsonDocument"), "WithoutMetadata").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("string"), "name")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(Cs.Symbol("stream", new() { AsVar = true, Using = true, Value = NodeNew("MemoryStream") }),
                Cs.Symbol("writer", new()
                {
                    AsVar = true,
                    Using = true,
                    Value = NodeNew("Utf8JsonWriter", Cs.Symbol("stream"))
                }), NodeCall("writer.WriteStartObject"),
                write, NodeCall("writer.WriteEndObject"), NodeCall("writer.Flush"),
                Return(NodeCall("JsonDocument.Parse", NodeCall("stream.ToArray"),
                    NodeCall("CreateDocumentOptions", Cs.Symbol("options"), Cs.Value(false)))))).End();
        root.Method(Cs.Void, "RequireFirstMetadata").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("string"), "name").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("AllowOutOfOrderMetadataProperties", new()
            { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(true)),
                    Cs.Instruction(InstructionKind.Return)), NodeVar("items", NodeCall("e.EnumerateObject")),
                NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { NodeCall("items.MoveNext") } }),
                    NodeThrow(NodeNew("JsonException"))), NodeIf(NodeBinary(CsOperator.AndAlso,
                        NodeBinary(CsOperator.NotEqual, Cs.Symbol("name"), Cs.Value("$id")),
                        NodeBinary(CsOperator.AndAlso, ReferenceCycleEmitter.IsPreserve(),
                            NodeBinary(CsOperator.Equal, NodeMember(NodeMember(Cs.Symbol("items"), "Current"), "Name"),
                                Cs.Value("$id")))), NodeIf(Cs.Symbol(new()
                                {
                                    Operator = CsOperator.Not,
                                    Parts = new CsNode[] { NodeCall("items.MoveNext") }
                                }), NodeThrow(NodeNew("JsonException")))),
                NodeIf(NodeBinary(CsOperator.NotEqual,
                    NodeMember(NodeMember(Cs.Symbol("items"), "Current"), "Name"), Cs.Symbol("name")),
                    NodeThrow(NodeNew("JsonException"))))).End();
    }
}
