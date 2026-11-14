using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class DomValue
{
    internal static string? Kind(ITypeSymbol type) => type.ToDisplayString() switch
    {
        "System.Text.Json.JsonElement" => "Element",
        "System.Text.Json.JsonDocument" => "Document",
        "System.Text.Json.Nodes.JsonNode" => "Node",
        _ => null
    };

    internal static void EmitWriter(List<CsNode> body, ITypeSymbol type)
    {
        string kind = Kind(type)!;
        body.Add(kind switch
        {
            "Element" => NodeCall("value.WriteTo", Cs.Symbol("w")),
            "Document" => NodeCall("value.RootElement.WriteTo", Cs.Symbol("w")),
            _ => NodeCall("value.WriteTo", Cs.Symbol("w"), Cs.Symbol("options"))
        });
    }

    internal static void EmitReader(List<CsNode> body, ITypeSymbol type)
    {
        CsNode value = Kind(type) switch
        {
            "Element" => NodeCall("e.Clone"),
            "Document" => NodeCall("JsonDocument.Parse", NodeCall("e.GetRawText")),
            _ => NonNull(NodeCall("JsonNode.Parse", NodeCall("e.GetRawText")))
        };
        body.Add(Return(value));
    }
}
