using System.Text.Json;

internal static class DemoCollections
{
    internal static void Run()
    {
        List<int> numbers = [1, 2, 3];
        string listJson = JsonSerializer.Serialize(numbers);
        DemoCheck.Expect("encode.list", listJson == "[1,2,3]", listJson);
        List<int> list = JsonSerializer.Deserialize<List<int>>(listJson)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.list", list.Count == 3 && list[0] == 1 && list[2] == 3,
            $"count={list.Count}");
        var values = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
        string mapJson = JsonSerializer.Serialize(values);
        DemoCheck.Expect("encode.dictionary", mapJson == "{\"one\":1,\"two\":2}", mapJson);
        Dictionary<string, int> map = JsonSerializer.Deserialize<Dictionary<string, int>>(mapJson)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.dictionary", map.Count == 2 && map["one"] == 1 && map["two"] == 2,
            $"count={map.Count}");
    }
}
