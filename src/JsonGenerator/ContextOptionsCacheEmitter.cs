using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class ContextOptionsCacheEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root)
    {
        var lazy = Cs.Generic("Lazy", Cs.Type("JsonSerializerOptions"));
        var map = Cs.Generic("System.Collections.Concurrent.ConcurrentDictionary", Cs.Type("string"), lazy);
        root.Field(map, "ContextOptions").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var factory = Cs.Lambda(new CsBody { Tree = Cs.Tree(Return(NodeNew("Lazy<JsonSerializerOptions>",
            Cs.Symbol("create")))) }, Cs.LambdaParameter("_"));
        root.Method(Cs.Type("JsonSerializerOptions"), "GetContextOptions").Internal().Static()
            .Parameter(Cs.Type("string"), "key").Parameter(Cs.Generic("Func", Cs.Type("JsonSerializerOptions")), "create")
            .Body(Cs.Tree(NodeVar("entry", NodeCall("ContextOptions.GetOrAdd", Cs.Symbol("key"), factory)),
                Return(NodeMember(Cs.Symbol("entry"), "Value")))).End();
    }
}
