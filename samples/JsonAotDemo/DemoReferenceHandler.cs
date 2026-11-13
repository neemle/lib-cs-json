using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DemoReferenceHandler : ReferenceHandler
{
    public override ReferenceResolver CreateResolver() => new DemoReferenceResolver();
}

internal sealed class DemoReferenceResolver : ReferenceResolver
{
    private readonly Dictionary<object, string> encoded = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, object> decoded = [];

    public override void AddReference(string referenceId, object value)
    {
        if (!decoded.TryAdd(referenceId, value)) throw new JsonException();
    }

    public override string GetReference(object value, out bool alreadyExists)
    {
        alreadyExists = encoded.TryGetValue(value, out string? reference);
        if (alreadyExists) return reference!;
        reference = "item-" + (encoded.Count + 1);
        encoded.Add(value, reference);
        return reference;
    }

    public override object ResolveReference(string referenceId) => decoded.TryGetValue(referenceId, out object? value) ?
        value : throw new JsonException();
}
