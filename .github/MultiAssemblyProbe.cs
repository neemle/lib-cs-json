using System.Text.Json;
using System.Text.Json.Serialization;

#if LIBRARY
public sealed record LibraryMessage(string Name);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(LibraryChild), "child")]
public record LibraryBase(string Name);
public sealed record LibraryChild(string Name) : LibraryBase(Name);
public static class Library
{
    public static string Encode()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UpperConverter());
        return JsonSerializer.Serialize(new LibraryMessage("Ada"), options);
    }

    public static string EncodePolymorphic()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UpperConverter());
        return JsonSerializer.Serialize<LibraryBase>(new LibraryChild("Ada"), options);
    }
}
#else
var options = new JsonSerializerOptions();
options.Converters.Add(new UpperConverter());
string encoded = JsonSerializer.Serialize(new LibraryMessage("Ada"), options);
if (Library.Encode() != "{\"Name\":\"ADA\"}" || encoded != Library.Encode() ||
    JsonSerializer.Deserialize<LibraryMessage>(encoded, options)?.Name != "ada")
    throw new InvalidOperationException("JSON output differs from the expected value.");
string polymorphic = Library.EncodePolymorphic();
if (JsonSerializer.Deserialize<LibraryBase>(polymorphic, options) is not LibraryChild { Name: "ada" })
    throw new InvalidOperationException("Cross-assembly polymorphism failed.");
Console.WriteLine("MULTI_ASSEMBLY_PASS");
#endif

internal sealed class UpperConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()?.ToLowerInvariant();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUpperInvariant());
}
