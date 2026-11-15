using System.Text.Json;
using System.Text.Json.Serialization;

#if LIBRARY
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("App")]

public sealed record LibraryMessage(string Name);
internal sealed record HiddenLibraryMessage(string Name);
[Obsolete("This non-JSON service must not become a metadata root.", true)]
public sealed class Repository(int id) { public int Id { get; } = id; }
public abstract record GatewayResult<T>
{
    public sealed record Success(T Value) : GatewayResult<T>;
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(LibraryChild), "child")]
public record LibraryBase(string Name);
public sealed record LibraryChild(string Name) : LibraryBase(Name);
public static class Library
{
    public static string Encode()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new LibraryUpperConverter());
        return JsonSerializer.Serialize(new LibraryMessage("Ada"), options);
    }

    public static string EncodePolymorphic()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new LibraryUpperConverter());
        return JsonSerializer.Serialize<LibraryBase>(new LibraryChild("Ada"), options);
    }

    public static string EncodeGateway()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new LibraryUpperConverter());
        return JsonSerializer.Serialize(new GatewayResult<int>.Success(42), options);
    }

    public static string EncodeHidden()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new LibraryUpperConverter());
        return JsonSerializer.Serialize(new HiddenLibraryMessage("Ada"), options);
    }
}
#else
var options = new JsonSerializerOptions();
options.Converters.Add(new UpperConverter());
string encoded = JsonSerializer.Serialize(new LibraryMessage("Ada"), options);
if (Library.Encode() != "{\"Name\":\"ADA\"}" || encoded != Library.Encode() ||
    JsonSerializer.Deserialize<LibraryMessage>(encoded, options)?.Name != "ada")
    throw new InvalidOperationException("JSON output differs from the expected value.");
if (Library.EncodeHidden() != "{\"Name\":\"ADA\"}")
    throw new InvalidOperationException("Library-local JSON metadata is missing.");
string polymorphic = Library.EncodePolymorphic();
if (JsonSerializer.Deserialize<LibraryBase>(polymorphic, options) is not LibraryChild { Name: "ada" })
    throw new InvalidOperationException("Cross-assembly polymorphism failed.");
if (JsonSerializer.Deserialize<GatewayResult<int>.Success>(Library.EncodeGateway(), options)?.Value != 42)
    throw new InvalidOperationException("Nested generic JSON type failed.");
string first = JsonSerializer.Serialize(new First.Entry("Ada"), options);
string second = JsonSerializer.Serialize(new Second.Entry("Ada"), options);
if (JsonSerializer.Deserialize<First.Entry>(first, options)?.Name != "ada" ||
    JsonSerializer.Deserialize<Second.Entry>(second, options)?.Name != "ada")
    throw new InvalidOperationException("JSON metadata type names collided.");
Console.WriteLine("MULTI_ASSEMBLY_PASS");
#endif

#if LIBRARY
internal sealed class LibraryUpperConverter : JsonConverter<string>
#else
internal sealed class UpperConverter : JsonConverter<string>
#endif
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()?.ToLowerInvariant();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUpperInvariant());
}

#if LIBRARY
namespace First { public sealed record Entry(string Name); }
namespace Second { public sealed record Entry(string Name); }
#endif
