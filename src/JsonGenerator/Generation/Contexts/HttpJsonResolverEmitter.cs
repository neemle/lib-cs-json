using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class HttpJsonResolverEmitter
{
    private const string Key = "Neemle.JSON.HttpResolvers";
    private const string Resolver = "System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver";
    private const string Collection = "System.Collections.Generic.List<" + Resolver + ">";

    internal static string Generate(string? contextName, bool exposed)
    {
        var document = Cs.Document(CsLanguageVersion.CSharp14).Namespace("Neemle.Json").Nullable(NullableMode.Enable);
        var root = document.Class("GeneratedJsonResolver").Sealed().Base(NodeType(Resolver));
        if (exposed) root.Internal(); else root.Modifier("file");
        root.Field(NodeType("GeneratedJsonResolver"), "Instance").Internal().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", []) { Type = Cs.Type("GeneratedJsonResolver") }).End();
        var read = new List<CsNode>
        {
            NodeVar("resolvers", Cast(Collection + "?", NodeCall("System.AppContext.GetData", Cs.Value(Key)))),
            NodeIf(NullTest(Cs.Symbol("resolvers")), Return(Cs.Value(null))),
            NodeEach("resolver", Cs.Symbol("resolvers"),
                NodeVar("info", NodeCall("resolver.GetTypeInfo", Cs.Symbol("type"), Cs.Symbol("options"))),
                NodeIf(NullTest(Cs.Symbol("info"), true), Return(Cs.Symbol("info")))),
            Return(Cs.Value(null))
        };
        root.Method(NodeType("System.Text.Json.Serialization.Metadata.JsonTypeInfo?"), "GetTypeInfo").Public()
            .Parameter(Cs.Type("System.Type"), "type").Parameter(Cs.Type("System.Text.Json.JsonSerializerOptions"), "options")
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.Lock, new()
            { Value = TypeOf("System.Text.Json.JsonSerializer"), Body = Cs.Tree(read.ToArray()) }))).End();
        if (contextName is not null)
        {
            var register = new CsNode[]
            {
                NodeVar("resolvers", NodeBinary(CsOperator.Coalesce,
                    Cast(Collection + "?", NodeCall("System.AppContext.GetData", Cs.Value(Key))), NodeNew(Collection))),
                NodeCall("resolvers.Add", NodeMember(Cs.Symbol(contextName), "Default")),
                NodeCall("System.AppContext.SetData", Cs.Value(Key), Cs.Symbol("resolvers"))
            };
            root.Method(Cs.Void, "Register").Internal().Static()
                .Attribute(Cs.Attribute(Cs.Type("System.Runtime.CompilerServices.ModuleInitializer")))
                .Body(Cs.Tree(Cs.Instruction(InstructionKind.Lock, new()
                { Value = TypeOf("System.Text.Json.JsonSerializer"), Body = Cs.Tree(register) }))).End();
        }
        root.End();
        return document.Render();
    }
}
