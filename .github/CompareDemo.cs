using System.Text;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CompareDemo <jit-output> <aot-output>");
    return 2;
}

string[] jit = File.ReadAllText(args[0]).Split('\n');
string[] aot = File.ReadAllText(args[1]).Split('\n');
if (jit.Length != aot.Length)
{
    Console.Error.WriteLine($"Different line counts: JIT {jit.Length}, AOT {aot.Length}.");
    return 1;
}

for (int i = 0; i < jit.Length; i++)
{
    if (Equivalent(jit[i], aot[i])) continue;
    Console.Error.WriteLine($"Different output at line {i + 1}:\nJIT: {jit[i]}\nAOT: {aot[i]}");
    return 1;
}

Console.WriteLine("JIT and AOT outputs match.");
return 0;

static bool Equivalent(string left, string right)
{
    if (string.Equals(left, right, StringComparison.Ordinal)) return true;
    const string separator = ": PASS ";
    int leftPayload = left.IndexOf(separator, StringComparison.Ordinal);
    int rightPayload = right.IndexOf(separator, StringComparison.Ordinal);
    if (leftPayload >= 0 && rightPayload >= 0 &&
        left.AsSpan(0, leftPayload + separator.Length).SequenceEqual(right.AsSpan(0, rightPayload + separator.Length)))
    {
        return EquivalentJsonSequence(left[(leftPayload + separator.Length)..], right[(rightPayload + separator.Length)..]);
    }
    return false;
}

static bool EquivalentJsonSequence(string left, string right)
{
    byte[] leftBytes = Encoding.UTF8.GetBytes(left);
    byte[] rightBytes = Encoding.UTF8.GetBytes(right);
    int leftOffset = 0;
    int rightOffset = 0;
    while (true)
    {
        using JsonDocument? leftJson = ParseJson(leftBytes, ref leftOffset);
        using JsonDocument? rightJson = ParseJson(rightBytes, ref rightOffset);
        if (leftJson is null || rightJson is null || !JsonElement.DeepEquals(leftJson.RootElement, rightJson.RootElement))
            return false;
        SkipWhitespace(leftBytes, ref leftOffset);
        SkipWhitespace(rightBytes, ref rightOffset);
        if (leftOffset == leftBytes.Length || rightOffset == rightBytes.Length)
            return leftOffset == leftBytes.Length && rightOffset == rightBytes.Length;
        if (leftBytes[leftOffset] is not ((byte)'/' or (byte)'|') ||
            leftBytes[leftOffset++] != rightBytes[rightOffset++]) return false;
    }
}

static JsonDocument? ParseJson(byte[] bytes, ref int offset)
{
    var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions { MaxDepth = int.MaxValue });
    try
    {
        JsonDocument result = JsonDocument.ParseValue(ref reader);
        offset += checked((int)reader.BytesConsumed);
        return result;
    }
    catch (JsonException) { return null; }
}

static void SkipWhitespace(byte[] bytes, ref int offset)
{
    while (offset < bytes.Length && bytes[offset] is (byte)' ' or (byte)'\t' or (byte)'\r') offset++;
}
