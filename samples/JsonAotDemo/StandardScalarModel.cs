public sealed class StandardScalarModel
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTimeOffset Offset { get; set; }
    public DateOnly Date { get; set; }
}
