using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
app.Urls.Add("http://127.0.0.1:0");
app.MapGet("/json", () => TypedResults.Json(new Message("Ada"), WebProbeContext.Default.Message));
if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("Reflection-based JSON serialization must be disabled.");
var action = ProbeAction.Created;
if ($"Action: {action}" != "Action: Created" || $"{action,10}" != "   Created")
    throw new InvalidOperationException("Enum interpolation failed.");

string json = JsonSerializer.Serialize(new Message("Ada"));
if (JsonSerializer.Deserialize<Message>(json)?.Name != "Ada")
    throw new InvalidOperationException("The JSON round trip failed.");

await app.StartAsync();
using var client = new HttpClient();
string response = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/json"));
await app.StopAsync();
if (JsonSerializer.Deserialize<Message>(response)?.Name != "Ada")
    throw new InvalidOperationException("The HTTP JSON round trip failed.");

Console.WriteLine("ASPNET_STARTUP_PASS");

public sealed record Message(string Name);
public enum ProbeAction { Created }

[JsonSerializable(typeof(Message))]
internal partial class WebProbeContext : JsonSerializerContext;
