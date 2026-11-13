using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class StaticMetadataEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root)
    {
        var empty = Cs.Symbol(new() { AsCall = true, Target = Cs.Symbol("Empty", new() { Target = TypeRef("System.Array"),
            TypeArguments = new[] { Cs.Type("JsonPropertyInfo") } }) });
        var initializer = Cs.Lambda(new CsBody { Tree = Cs.Tree(Return(empty)) }, Cs.LambdaParameter("_"))
            with { IsStatic = true };
        var objectValues = Cs.Symbol(new() { AsNew = true, Type = Cs.Generic("JsonObjectInfoValues", Cs.Type("T")),
            Initializer = Cs.Symbol(new() { AsInitializer = true, Parts = new CsNode[] {
                NodeAssign(Cs.Symbol("PropertyMetadataInitializer"), initializer) } }) });
        var objectCall = Cs.Symbol(new() { AsCall = true, Target = Cs.Symbol("CreateObjectInfo", new()
            { Target = TypeRef("JsonMetadataServices"), TypeArguments = new[] { Cs.Type("T") } }),
            Arguments = Cs.Compound(Cs.Symbol("options"), objectValues) });
        root.Method(Cs.Generic("JsonTypeInfo", Cs.Type("T")), "CreateStaticObjectInfo").Internal().Static()
            .Generic("T", Cs.Constraint("notnull")).Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(Return(objectCall))).End();

        var info = Cs.Symbol(new() { AsNew = true, Type = Cs.Generic("JsonPropertyInfoValues", Cs.Type("T")),
            Initializer = Cs.Symbol(new() { AsInitializer = true, Parts = new CsNode[] {
                NodeAssign(Cs.Symbol("IsProperty"), Cs.Value(true)),
                NodeAssign(Cs.Symbol("IsPublic"), Cs.Value(true)),
                NodeAssign(Cs.Symbol("DeclaringType"), NodeMember(Cs.Symbol("owner"), "Type")),
                NodeAssign(Cs.Symbol("PropertyName"), Cs.Symbol("name")),
                NodeAssign(Cs.Symbol("JsonPropertyName"), Cs.Symbol("name")) } }) });
        var propertyCall = Cs.Symbol(new() { AsCall = true, Target = Cs.Symbol("CreatePropertyInfo", new()
            { Target = TypeRef("JsonMetadataServices"), TypeArguments = new[] { Cs.Type("T") } }),
            Arguments = Cs.Compound(NodeMember(Cs.Symbol("owner"), "Options"), info) });
        root.Method(Cs.Type("JsonPropertyInfo"), "CreateStaticPropertyInfo").Internal().Static().Generic("T")
            .Parameter(Cs.Type("JsonTypeInfo"), "owner").Parameter(Cs.Type("string"), "name")
            .Body(Cs.Tree(Return(propertyCall))).End();
    }
}
