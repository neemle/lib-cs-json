using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class EnumConverterRegistryEmitter
{
    private const string Key = "Neemle.JSON.EnumConverters";
    private const string Registry = "System.Collections.Generic.Dictionary<System.Type, System.Func<System.Text.Json.JsonNamingPolicy?, bool, System.Text.Json.Serialization.JsonConverter>>";

    internal static string? Generate(IReadOnlyList<ITypeSymbol> types, string assembly)
    {
        var enums = types.Select((type, index) => (type, index)).Where(item => item.type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum &&
            ConverterAttributeEmitter.Converter(item.type, item.type) is null).ToArray();
        if (enums.Length == 0) return null;
        var document = Cs.Document(CsLanguageVersion.CSharp14).Nullable(NullableMode.Enable);
        var root = document.Class("GeneratedEnumConverters").Internal().Static();
        var registrations = new List<CsNode>();
        foreach (var (type, index) in enums)
        {
            string name = TypeName(type);
            string codec = GeneratedAssemblyNames.Name("GeneratedJsonCodec_" + index, assembly);
            string adapterName = "EnumConverter_" + index;
            var adapter = root.Class(adapterName).Private().Sealed()
                .Base(Cs.Generic("System.Text.Json.Serialization.JsonConverter", NodeType(name)));
            adapter.Field(NodeType("System.Text.Json.JsonNamingPolicy?"), "policy").Private().ReadOnly().End();
            adapter.Field(Cs.Bool, "allowInteger").Private().ReadOnly().End();
            adapter.Constructor().Public().Parameter(NodeType("System.Text.Json.JsonNamingPolicy?"), "valuePolicy")
                .Parameter(Cs.Bool, "valueAllowInteger").Body(
                    Cs.Statement(Cs.Assign(Cs.Id("policy"), Cs.Id("valuePolicy"))),
                    Cs.Statement(Cs.Assign(Cs.Id("allowInteger"), Cs.Id("valueAllowInteger")))).End();
            adapter.Method(NodeType("System.Text.Json.JsonSerializerOptions"), "Prepare").Private()
                .Parameter(NodeType("System.Text.Json.JsonSerializerOptions"), "options")
                .Body(Cs.Tree(NodeVar("copy", NodeNew("System.Text.Json.JsonSerializerOptions", Cs.Symbol("options"))),
                    NodeCall("copy.Converters.Clear"), NodeAssign(NodeMember(Cs.Symbol("copy"), "TypeInfoResolver"), Cs.Value(null)),
                    NodeCall(codec + ".RegisterEnum_" + index, Cs.Symbol("copy"),
                        Cs.Symbol("policy"), Cs.Symbol("allowInteger")), Return(Cs.Symbol("copy")))).End();
            adapter.Method(NodeType(name), "Read").Public().Override()
                .Parameter(NodeType("System.Text.Json.Utf8JsonReader"), "reader", "ref")
                .Parameter(Cs.Type("System.Type"), "typeToConvert")
                .Parameter(NodeType("System.Text.Json.JsonSerializerOptions"), "options")
                .Body(Cs.Tree(Return(NodeCall(codec + ".Decode_" + index,
                    Arg(ArgumentModifier.Ref, Cs.Symbol("reader")), NodeCall("Prepare", Cs.Symbol("options")))))).End();
            adapter.Method(Cs.Void, "Write").Public().Override()
                .Parameter(NodeType("System.Text.Json.Utf8JsonWriter"), "writer")
                .Parameter(NodeType(name), "value").Parameter(NodeType("System.Text.Json.JsonSerializerOptions"), "options")
                .Body(Cs.Tree(NodeCall(codec + ".Encode_" + index, Cs.Symbol("writer"), Cs.Symbol("value"),
                    NodeCall("Prepare", Cs.Symbol("options"))))).End();
            adapter.End();
            var factory = Cs.Lambda(new CsBody { Tree = Cs.Tree(Return(NodeNew(adapterName, Cs.Symbol("policy"), Cs.Symbol("allowInteger")))) },
                Cs.LambdaParameter("policy"), Cs.LambdaParameter("allowInteger")) with
            { IsStatic = true };
            registrations.Add(NodeCall("registry.TryAdd", TypeOf(name), factory));
        }
        var body = new List<CsNode>
        {
            NodeVar("registry", NodeBinary(CsOperator.Coalesce,
                Cast(Registry + "?", NodeCall("System.AppContext.GetData", Cs.Value(Key))), NodeNew(Registry)))
        };
        body.AddRange(registrations);
        body.Add(NodeCall("System.AppContext.SetData", Cs.Value(Key), Cs.Symbol("registry")));
        root.Method(Cs.Void, "Initialize").Internal().Static()
            .Attribute(Cs.Attribute(Cs.Type("System.Runtime.CompilerServices.ModuleInitializer")))
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.Lock, new()
            { Value = TypeOf("System.Text.Json.JsonSerializer"), Body = Cs.Tree(body.ToArray()) }))).End();
        root.End();
        return document.Render();
    }
}
