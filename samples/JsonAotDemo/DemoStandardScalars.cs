using System.Text.Json;

internal static class DemoStandardScalars
{
    internal static void Run()
    {
        var id = Guid.Parse("a987fbc9-4bed-3078-cf07-9141ba07c9f3");
        string idJson = JsonSerializer.Serialize(id);
        DemoCheck.Expect("guid.encode", idJson == "\"a987fbc9-4bed-3078-cf07-9141ba07c9f3\"", idJson);
        DemoCheck.Expect("guid.decode", JsonSerializer.Deserialize<Guid>(idJson) == id, idJson);
        var timestamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string dateJson = JsonSerializer.Serialize(timestamp);
        DemoCheck.Expect("datetime.encode", dateJson == "\"2024-01-02T03:04:05Z\"", dateJson);
        DemoCheck.Expect("datetime.decode", JsonSerializer.Deserialize<DateTime>(dateJson) == timestamp, dateJson);
        var offset = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        string offsetJson = JsonSerializer.Serialize(offset);
        DemoCheck.Expect("datetime-offset.encode", offsetJson == "\"2024-01-02T03:04:05+02:00\"", offsetJson);
        DemoCheck.Expect("datetime-offset.decode", JsonSerializer.Deserialize<DateTimeOffset>(offsetJson) == offset,
            offsetJson);
        var date = new DateOnly(2024, 2, 29);
        string dateOnlyJson = JsonSerializer.Serialize(date);
        DemoCheck.Expect("dateonly.encode", dateOnlyJson == "\"2024-02-29\"", dateOnlyJson);
        DemoCheck.Expect("dateonly.decode", JsonSerializer.Deserialize<DateOnly>(dateOnlyJson) == date, dateOnlyJson);
        Model(id, timestamp, offset);
        ReferenceScalars();
        InvalidGuid();
        Times();
    }

    private static void ReferenceScalars()
    {
        var uri = new Uri("https://example.com/a%20b");
        string uriJson = JsonSerializer.Serialize(uri);
        var readUri = JsonSerializer.Deserialize<Uri>(uriJson);
        DemoCheck.Expect("uri.roundtrip", readUri?.OriginalString == uri.OriginalString, uriJson);
        var relative = new Uri("a%20b", UriKind.Relative);
        string relativeJson = JsonSerializer.Serialize(relative);
        DemoCheck.Expect("uri.relative", JsonSerializer.Deserialize<Uri>(relativeJson)?.OriginalString ==
            relative.OriginalString, relativeJson);
        var version = new Version(1, 2, 3, 4);
        string versionJson = JsonSerializer.Serialize(version);
        DemoCheck.Expect("version.roundtrip", JsonSerializer.Deserialize<Version>(versionJson) == version, versionJson);
    }

    private static void Model(Guid id, DateTime timestamp, DateTimeOffset offset)
    {
        var source = new StandardScalarModel { Id = id, Timestamp = timestamp, Offset = offset,
            Date = new DateOnly(2024, 2, 29) };
        string json = JsonSerializer.Serialize(source);
        var result = JsonSerializer.Deserialize<StandardScalarModel>(json)!;
        DemoCheck.Expect("framework-scalars.model", result.Id == id && result.Timestamp == timestamp &&
            result.Offset.EqualsExact(offset) && result.Date == source.Date, json);
        Guid? nullable = JsonSerializer.Deserialize<Guid?>(JsonSerializer.Serialize<Guid?>(id));
        DemoCheck.Expect("guid.nullable", nullable == id, nullable.ToString() ?? "null");
    }

    private static void InvalidGuid()
    {
        try
        {
            JsonSerializer.Deserialize<Guid>("\"not-a-guid\"");
            DemoCheck.Expect("guid.invalid", false, "accepted invalid value");
        }
        catch (JsonException error)
        {
            DemoCheck.Expect("guid.invalid", error.Path == "$", error.Path ?? "null");
        }
    }

    private static void Times()
    {
        var duration = new TimeSpan(0, 1, 2, 3).Add(TimeSpan.FromTicks(4));
        string durationJson = JsonSerializer.Serialize(duration);
        DemoCheck.Expect("timespan.encode", durationJson == "\"01:02:03.0000004\"", durationJson);
        DemoCheck.Expect("timespan.decode", JsonSerializer.Deserialize<TimeSpan>(durationJson) == duration,
            durationJson);
        var time = new TimeOnly(1, 2, 3).Add(TimeSpan.FromTicks(4));
        string timeJson = JsonSerializer.Serialize(time);
        DemoCheck.Expect("timeonly.encode", timeJson == durationJson, timeJson);
        DemoCheck.Expect("timeonly.decode", JsonSerializer.Deserialize<TimeOnly>(timeJson) == time, timeJson);
        InvalidTime();
        Character();
    }

    private static void InvalidTime()
    {
        try
        {
            JsonSerializer.Deserialize<TimeSpan>("\"3:5:5.\"");
            DemoCheck.Expect("timespan.invalid", false, "accepted invalid value");
        }
        catch (JsonException error)
        {
            DemoCheck.Expect("timespan.invalid", error.Path == "$", error.Path ?? "null");
        }
    }

    private static void Character()
    {
        string json = JsonSerializer.Serialize('A');
        DemoCheck.Expect("char.encode", json == "\"A\"", json);
        DemoCheck.Expect("char.decode", JsonSerializer.Deserialize<char>(json) == 'A', json);
        try
        {
            JsonSerializer.Deserialize<char>("\"ab\"");
            DemoCheck.Expect("char.invalid", false, "accepted invalid value");
        }
        catch (JsonException error)
        {
            DemoCheck.Expect("char.invalid", error.Path == "$", error.Path ?? "null");
        }
    }
}
