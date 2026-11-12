using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoPopulate
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions
        {
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
        };
        PopulateCase value = JsonSerializer.Deserialize<PopulateCase>(
            "{\"Items\":[2],\"Value\":{\"Number\":9}}", options)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("populate.readonly-list", value.Items.Count == 2
            && value.Items[0] == 1 && value.Items[1] == 2,
            $"count={value.Items.Count}");
        DemoCheck.Expect("populate.readonly-value", value.Value.Number == 3,
            $"number={value.Value.Number}");
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<PopulateInvalid>("{}"); }
        catch (InvalidOperationException error)
        {
            rejected = error.Message.Contains("setter", StringComparison.OrdinalIgnoreCase);
        }
        DemoCheck.Expect("populate.value-requires-setter", rejected, rejected ? "rejected" : "accepted");
    }
}
