using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class AsyncSequenceWriterEmitter
{
    internal static void EmitPublic(TypeBuilder<FileBuilder> root, int index, string type, bool reference)
    {
        var sequence = Cs.Generic("System.Collections.Generic.IAsyncEnumerable", InputType(type, reference)).Nullable();
        root.Method(Cs.Type("Task"), $"EncodeAsyncSequence_{index}").Internal().Static().Async()
            .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(sequence, "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Parameter(Cs.Type("CancellationToken"), "cancellationToken",
                @default: new CsExpression(ExpressionKind.Default, "", []))
            .Body(Cs.Tree(NodeCall("ArgumentNullException.ThrowIfNull", Cs.Symbol("utf8Json")),
                NodeCall("ValidateRuntimeOptions", Cs.Symbol("options")),
                Cs.Symbol("writer", new()
                {
                    AsVar = true,
                    Using = true,
                    Value = NodeNew("Utf8JsonWriter", Cs.Symbol("utf8Json"), WriterOptions())
                }),
                NodeIf(NullTest(Cs.Symbol("value")), NodeCall("writer.WriteNullValue"),
                    Cs.Symbol("FlushAsync", new()
                    {
                        Target = Cs.Symbol("writer"),
                        AsCall = true,
                        Await = true,
                        Arguments = Cs.Compound(Cs.Symbol("cancellationToken"))
                    }), Cs.Instruction(InstructionKind.Return)),
                Cs.Symbol($"WriteAsyncSequence_{index}", new()
                {
                    AsCall = true,
                    Await = true,
                    Arguments = Cs.Compound(Cs.Symbol("writer"), Cs.Symbol("value"), Cs.Symbol("options"),
                        Cs.Symbol("cancellationToken"))
                }))).End();
        string sequenceName = "System.Collections.Generic.IAsyncEnumerable<" + type + ">";
        root.Method(Cs.Type("Task"), $"EncodeDeclaredAsyncSequence_{index}").Internal().Static()
            .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(Cs.Type("object").Nullable(), "value")
            .Parameter(Cs.Type("Type"), "inputType")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Parameter(Cs.Type("CancellationToken"), "cancellationToken",
                @default: new CsExpression(ExpressionKind.Default, "", []))
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.NotEqual, Cs.Symbol("inputType"), TypeOf(sequenceName)),
                    NodeThrow(NodeNew("NotSupportedException"))),
                Return(NodeCall($"EncodeAsyncSequence_{index}", Cs.Symbol("utf8Json"), CoerceValue(sequenceName, true),
                    Cs.Symbol("options"), Cs.Symbol("cancellationToken"))))).End();
    }

    internal static void Emit(TypeBuilder<FileBuilder> root, int index, string type, bool reference)
    {
        var sequence = Cs.Generic("System.Collections.Generic.IAsyncEnumerable", InputType(type, reference));
        var loop = Cs.Instruction(InstructionKind.While, new()
        {
            Condition = Cs.Symbol("MoveNextAsync", new()
            { Target = Cs.Symbol("enumerator"), AsCall = true, Await = true }),
            Body = Cs.Tree(
                NodeCall($"Write_{index}", Cs.Symbol("writer"), NodeMember(Cs.Symbol("enumerator"), "Current"),
                    Cs.Symbol("options"), NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Value("$["),
                        Cs.Symbol("ToString", new()
                        {
                            Target = Cs.Symbol("position"),
                            AsCall = true,
                            Arguments = Cs.Compound(NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"))
                        })),
                        Cs.Value("]"))),
                NodeAssign(Cs.Symbol("position"), NodeBinary(CsOperator.Add, Cs.Symbol("position"), Cs.Value(1))),
                Cs.Symbol("FlushAsync", new()
                {
                    Target = Cs.Symbol("writer"),
                    AsCall = true,
                    Await = true,
                    Arguments = Cs.Compound(Cs.Symbol("cancellationToken"))
                }))
        });
        root.Method(Cs.Type("Task"), $"WriteAsyncItems_{index}").Private().Static().Async()
            .Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Generic("System.Collections.Generic.IAsyncEnumerator", InputType(type, reference)), "enumerator")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("CancellationToken"), "cancellationToken")
            .Body(Cs.Tree(NodeCall("writer.WriteStartArray"), NodeVar("position", Cs.Value(0)), loop, NodeCall("writer.WriteEndArray"),
                Cs.Symbol("FlushAsync", new()
                {
                    Target = Cs.Symbol("writer"),
                    AsCall = true,
                    Await = true,
                    Arguments = Cs.Compound(Cs.Symbol("cancellationToken"))
                }))).End();
        var work = new CsBody
        {
            Tree = Cs.Tree(Cs.Symbol($"WriteAsyncItems_{index}", new()
            {
                AsCall = true,
                Await = true,
                Arguments = Cs.Compound(Cs.Symbol("writer"), Cs.Symbol("enumerator"), Cs.Symbol("options"),
                Cs.Symbol("cancellationToken"))
            }))
        };
        var cleanup = new CsBody
        {
            Tree = Cs.Tree(Cs.Symbol("DisposeAsync", new()
            {
                Target = Cs.Symbol("enumerator"),
                AsCall = true,
                Await = true
            }), NodeCall("ExitCycle", Cs.Symbol("writer"), Cs.Symbol("options"),
                Cs.Value(false)))
        };
        root.Method(Cs.Type("Task"), $"WriteAsyncSequence_{index}").Private().Static().Async()
            .Parameter(Cs.Type("Utf8JsonWriter"), "writer").Parameter(sequence, "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("CancellationToken"), "cancellationToken")
            .Body(Cs.Tree(NodeVar("enumerator", NodeCall("value.GetAsyncEnumerator", Cs.Symbol("cancellationToken"))),
                NodeIf(Cs.Symbol(new()
                {
                    Operator = CsOperator.Not,
                    Parts = new CsNode[] {
                    NodeCall("EnterCycle", Cs.Symbol("writer"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Value(false)) }
                }),
                    Cs.Symbol("DisposeAsync", new() { Target = Cs.Symbol("enumerator"), AsCall = true, Await = true }),
                    Cs.Instruction(InstructionKind.Return)), Cs.Try(work, finallyBody: cleanup))).End();
    }
}
