using System.Runtime.CompilerServices;
using System.Text.Json;
#if !NATIVE_AOT
using System.Security.Cryptography;
#endif

#if !NATIVE_AOT
// A higher-servicing shared framework can shadow a source fork with the same
// assembly identity. Observe the ordinary application binding before its JSON call.
string serializerPath = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
    .Split(Path.PathSeparator)
    .SingleOrDefault(path => Path.GetFileName(path) == "System.Text.Json.dll")
    ?? throw new InvalidOperationException("The managed host did not expose the selected serializer path.");
string serializerHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(serializerPath)));
Console.WriteLine($"serializer-identity: {typeof(JsonSerializer).Assembly.FullName}");
Console.WriteLine($"serializer: {serializerPath}");
Console.WriteLine($"serializer-sha256: {serializerHash}");
if (args.Length != 2 || args[0] != "--expected-fork-sha256")
{
    Console.Error.WriteLine("Supply --expected-fork-sha256 with the source-built serializer's SHA256; see README.md.");
    return 2;
}
if (!string.Equals(serializerHash, args[1], StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("FAIL: the ordinary application did not load the expected source-built serializer.");
    return 1;
}
#endif

Console.WriteLine(Roundtrip.Run());
Console.WriteLine("roundtrip: PASS");
return 0;

internal static class Roundtrip
{
    // Keep the JSON exercise separate from managed binding diagnostics.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string Run()
    {
        var original = new Order
        {
            Id = 42,
            Customer = "musl",
            Lines = [new OrderLine { Sku = "json", Quantity = 2 }]
        };
        const string expected = "{\"Id\":42,\"Customer\":\"musl\",\"Lines\":[{\"Sku\":\"json\",\"Quantity\":2}]}";
        string json = JsonSerializer.Serialize(original);
        Order restored = JsonSerializer.Deserialize<Order>(json)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        if (json != expected || restored.Id != 42 || restored.Customer != "musl"
            || restored.Lines.Count != 1 || restored.Lines[0].Sku != "json"
            || restored.Lines[0].Quantity != 2)
        {
            throw new InvalidOperationException("JSON output or roundtrip differs from the expected values.");
        }
        return json;
    }
    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
        public List<OrderLine> Lines { get; set; } = [];
    }

    public sealed class OrderLine
    {
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }
}
