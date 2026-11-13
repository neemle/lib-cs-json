using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DemoLowLevel
{
    internal static void Run(string json)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", "Ada");
            writer.WriteNumber("count", 2);
            writer.WriteEndObject();
        }
        string written = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("writer.encode", written == "{\"name\":\"Ada\",\"count\":2}", written);
        var reader = new Utf8JsonReader(stream.ToArray());
        bool read = reader.Read() && reader.TokenType == JsonTokenType.StartObject
            && reader.Read() && reader.GetString() == "name"
            && reader.Read() && reader.GetString() == "Ada"
            && reader.Read() && reader.GetString() == "count"
            && reader.Read() && reader.GetInt32() == 2
            && reader.Read() && reader.TokenType == JsonTokenType.EndObject && !reader.Read();
        DemoCheck.Expect("reader.decode", read, read ? "name=Ada, count=2" : "unexpected tokens");
        using JsonDocument document = JsonDocument.Parse(json);
        DemoCheck.Expect("document.decode", document.RootElement.GetProperty("Number").GetInt32() == 7,
            document.RootElement.GetRawText());
        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Missing JSON node.");
        node["Number"] = 9;
        string updated = node.ToJsonString();
        using JsonDocument updatedDocument = JsonDocument.Parse(updated);
        DemoCheck.Expect("node.encode-decode", updatedDocument.RootElement.GetProperty("Number").GetInt32() == 9,
            updated);
        SerializerDom();
        SerializerReaderWriter();
        SerializerDeclaredType();
    }

    private static void SerializerDom()
    {
        var value = new NamingCaseModel { DisplayName = "Ada" };
        JsonElement element = JsonSerializer.SerializeToElement(value);
        var fromElement = JsonSerializer.Deserialize<NamingCaseModel>(element);
        DemoCheck.Expect("serializer.element", fromElement?.DisplayName == "Ada", element.GetRawText());
        using JsonDocument document = JsonSerializer.SerializeToDocument(value);
        var fromDocument = JsonSerializer.Deserialize<NamingCaseModel>(document);
        DemoCheck.Expect("serializer.document", fromDocument?.DisplayName == "Ada", document.RootElement.GetRawText());
        JsonNode? node = JsonSerializer.SerializeToNode(value);
        var fromNode = JsonSerializer.Deserialize<NamingCaseModel>(node);
        DemoCheck.Expect("serializer.node", fromNode?.DisplayName == "Ada", node?.ToJsonString() ?? "null");
    }

    private static void SerializerReaderWriter()
    {
        var value = new NamingCaseModel { DisplayName = "Ada" };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, value);
        byte[] bytes = stream.ToArray();
        var reader = new Utf8JsonReader(bytes);
        var fromReader = JsonSerializer.Deserialize<NamingCaseModel>(ref reader);
        DemoCheck.Expect("serializer.reader-writer", fromReader?.DisplayName == "Ada", Encoding.UTF8.GetString(bytes));
        var fromBytes = JsonSerializer.Deserialize<NamingCaseModel>((ReadOnlySpan<byte>)bytes);
        DemoCheck.Expect("serializer.byte-span", fromBytes?.DisplayName == "Ada", Encoding.UTF8.GetString(bytes));
        var fromChars = JsonSerializer.Deserialize<NamingCaseModel>("{\"DisplayName\":\"Ada\"}".AsSpan());
        DemoCheck.Expect("serializer.char-span", fromChars?.DisplayName == "Ada", fromChars?.DisplayName ?? "null");
    }

    private static void SerializerDeclaredType()
    {
        object value = new NamingCaseModel { DisplayName = "Ada" };
        JsonElement element = JsonSerializer.SerializeToElement(value, typeof(NamingCaseModel));
        object? result = JsonSerializer.Deserialize(element, typeof(NamingCaseModel));
        DemoCheck.Expect("serializer.declared-element", result is NamingCaseModel { DisplayName: "Ada" },
            element.GetRawText());
        using JsonDocument document = JsonSerializer.SerializeToDocument(value, typeof(NamingCaseModel));
        JsonNode? node = JsonSerializer.SerializeToNode(value, typeof(NamingCaseModel));
        object? fromNode = JsonSerializer.Deserialize(node, typeof(NamingCaseModel));
        DemoCheck.Expect("serializer.declared-node", fromNode is NamingCaseModel { DisplayName: "Ada" },
            document.RootElement.GetRawText());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, value, typeof(NamingCaseModel));
        var reader = new Utf8JsonReader(stream.ToArray());
        object? fromReader = JsonSerializer.Deserialize(ref reader, typeof(NamingCaseModel));
        DemoCheck.Expect("serializer.declared-reader-writer", fromReader is NamingCaseModel { DisplayName: "Ada" },
            Encoding.UTF8.GetString(stream.ToArray()));
        Type selected = value is NamingCaseModel ? typeof(NamingCaseModel) : typeof(string);
        using var dynamicStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(dynamicStream)) JsonSerializer.Serialize(writer, value, selected);
        var dynamicReader = new Utf8JsonReader(dynamicStream.ToArray());
        object? dynamicCopy = JsonSerializer.Deserialize(ref dynamicReader, selected);
        DemoCheck.Expect("serializer.runtime-type-reader-writer", dynamicCopy is NamingCaseModel { DisplayName: "Ada" },
            Encoding.UTF8.GetString(dynamicStream.ToArray()));
    }
}
