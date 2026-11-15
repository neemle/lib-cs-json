using System.Text.Json;

#if LIBRARY
public static class Library
{
    public static string Encode() => JsonSerializer.Serialize(1);
}
#else
if (Library.Encode() + JsonSerializer.Serialize(2) != "12")
    throw new InvalidOperationException("JSON output differs from the expected value.");
Console.WriteLine("MULTI_ASSEMBLY_PASS");
#endif
