using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoCallbacks
{
    internal static string Trace = "";

    internal static void Run()
    {
        Trace = "";
        string json = JsonSerializer.Serialize(new CallbackModel { Value = 7 });
        var copy = JsonSerializer.Deserialize<CallbackModel>(json);
        DemoCheck.Expect("callbacks.class", json == "{\"Value\":8}" && copy?.Value == 8 && Trace == "SsDd",
            $"{json} {Trace}");
        ValueType();
        Trace = "";
        var constructed = JsonSerializer.Deserialize<CallbackConstructorModel>("{\"Value\":3}");
        DemoCheck.Expect("callbacks.constructor", constructed?.Value == 3 && Trace == "Dd", Trace);
    }
    private static void ValueType()
    {
        Trace = "";
        string json = JsonSerializer.Serialize(new CallbackValueModel { Value = 7 });
        var copy = JsonSerializer.Deserialize<CallbackValueModel>(json);
        DemoCheck.Expect("callbacks.struct", json == "{\"Value\":8}" && copy.Value == 9 && Trace == "Sd",
            $"{json} {Trace}");
    }
}

public struct CallbackValueModel : IJsonOnSerializing, IJsonOnDeserialized
{
    public int Value { get; set; }
    void IJsonOnSerializing.OnSerializing() { DemoCallbacks.Trace += "S"; Value++; }
    void IJsonOnDeserialized.OnDeserialized() { DemoCallbacks.Trace += "d"; Value++; }
}

public sealed class CallbackModel : IJsonOnSerializing, IJsonOnSerialized, IJsonOnDeserializing, IJsonOnDeserialized
{
    public int Value { get; set; }

    void IJsonOnSerializing.OnSerializing() { DemoCallbacks.Trace += "S"; Value++; }
    void IJsonOnSerialized.OnSerialized() => DemoCallbacks.Trace += "s";
    void IJsonOnDeserializing.OnDeserializing() => DemoCallbacks.Trace += "D";
    void IJsonOnDeserialized.OnDeserialized() => DemoCallbacks.Trace += "d";
}

public sealed class CallbackConstructorModel(int value) : IJsonOnDeserializing, IJsonOnDeserialized
{
    public int Value { get; } = value;
    void IJsonOnDeserializing.OnDeserializing() => DemoCallbacks.Trace += "D";
    void IJsonOnDeserialized.OnDeserialized() => DemoCallbacks.Trace += "d";
}
