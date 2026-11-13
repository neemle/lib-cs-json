using System.Text.Json.Serialization;

public sealed class InternalConstructorModel
{
    [JsonConstructor]
    internal InternalConstructorModel(string name) => Name = name;

    public string Name { get; }
}

public sealed class InternalEmptyConstructorModel
{
    [JsonConstructor]
    internal InternalEmptyConstructorModel() { }

    public int Number { get; set; }
}

public class SharedConstructorModel
{
    [JsonConstructor]
    protected internal SharedConstructorModel(int number) => Number = number;

    public int Number { get; }
}

public sealed class PrivateConstructorModel
{
    [JsonConstructor]
    private PrivateConstructorModel(int number) => Number = number;

    public int Number { get; }
}

public sealed class PrivateEmptyConstructorModel
{
    [JsonConstructor]
    private PrivateEmptyConstructorModel() { }

    public int Number { get; set; }
}
