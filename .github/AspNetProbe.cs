using System.Text.Json;
using System.Net.Http.Json;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Neemle.Json.GeneratedJsonResolver.Instance));
var app = builder.Build();
app.Urls.Add("http://127.0.0.1:0");
app.MapGet("/json", () => new Message("Ada"));
app.MapGet("/ok", () => Results.Ok(new Message("Ada")));
app.MapGet("/typed", () => TypedResults.Ok(new Message("Ada")));
app.MapGet("/typed-json", () => TypedResults.Json(new Message("Ada")));
app.MapGet("/array", () => new[] { new Message("Ada") });
app.MapPost("/echo", (Message message) => Results.Json(message));
app.MapGet("/response", async (HttpContext context) => await context.Response.WriteAsJsonAsync(new Message("Ada")));
app.MapPost("/body", async (HttpRequest request) => TypedResults.Ok(await request.ReadFromJsonAsync<Message>()));
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
string okResponse = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/ok"));
string typedResponse = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/typed"));
string typedJson = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/typed-json"));
string arrayJson = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/array"));
using var payload = new StringContent("{\"name\":\"Ada\"}", System.Text.Encoding.UTF8, "application/json");
using var reply = await client.PostAsync(new Uri(new Uri(app.Urls.Single()), "/echo"), payload);
string echo = await reply.Content.ReadAsStringAsync();
string responseJson = await client.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/response"));
using var body = new StringContent("{\"name\":\"Ada\"}", System.Text.Encoding.UTF8, "application/json");
using var bodyReply = await client.PostAsync(new Uri(new Uri(app.Urls.Single()), "/body"), body);
var bodyMessage = await bodyReply.Content.ReadFromJsonAsync<Message>();
await app.StopAsync();
var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (JsonSerializer.Deserialize<Message[]>(arrayJson, webOptions)?[0].Name != "Ada" ||
    bodyMessage?.Name != "Ada" || new[] { response, okResponse, typedResponse, typedJson, echo, responseJson }.Any(value =>
        JsonSerializer.Deserialize<Message>(value, webOptions)?.Name != "Ada"))
    throw new InvalidOperationException("The HTTP JSON round trip failed.");

Console.WriteLine("ASPNET_STARTUP_PASS");

public sealed record Message(string Name);
public enum ProbeAction { Created }
