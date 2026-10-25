// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Neemle.DBAL.JsonEnumConformance;

internal static class Checks
{
 internal static void Run()
 {
  Observe("default",()=>JsonSerializer.Serialize(WireStatus.DefaultName,EnumContext.Default.WireStatus));
  Observe("attribute",()=>JsonSerializer.Serialize(WireStatus.Renamed,EnumContext.Default.WireStatus));
  Observe("alias",()=>JsonSerializer.Serialize(WireStatus.Alias,EnumContext.Default.WireStatus));
  Observe("read-attribute",()=>JsonSerializer.Deserialize("\"wire-value\"",EnumContext.Default.WireStatus).ToString());
  Observe("read-attribute-case",()=>JsonSerializer.Deserialize("\"WIRE-VALUE\"",EnumContext.Default.WireStatus).ToString());
  Observe("read-default-case",()=>JsonSerializer.Deserialize("\"defaultname\"",EnumContext.Default.WireStatus).ToString());
  Observe("read-unknown",()=>JsonSerializer.Deserialize("\"missing\"",EnumContext.Default.WireStatus).ToString());
  Observe("flags",()=>JsonSerializer.Serialize(WireFlags.First|WireFlags.Second,EnumContext.Default.WireFlags));
  Observe("read-flags",()=>JsonSerializer.Deserialize("\"one, Second\"",EnumContext.Default.WireFlags).ToString());
  Observe("array",()=>JsonSerializer.Serialize(new[]{WireStatus.DefaultName,WireStatus.Renamed},EnumContext.Default.WireStatusArray));
  Observe("nullable",()=>JsonSerializer.Serialize(new EnumEnvelope{Value=WireStatus.Renamed},EnumContext.Default.EnumEnvelope));
  var options=new JsonSerializerOptions();options.Converters.Add(new JsonStringEnumConverter<WireStatus>(JsonNamingPolicy.SnakeCaseLower));var policy=new EnumContext(options);
  Observe("policy-default",()=>JsonSerializer.Serialize(WireStatus.DefaultName,policy.WireStatus));
  Observe("policy-attribute",()=>JsonSerializer.Serialize(WireStatus.Renamed,policy.WireStatus));
  Observe("invalid-empty",()=>JsonSerializer.Serialize(EmptyName.Value,EnumContext.Default.EmptyName));
  Observe("invalid-whitespace",()=>JsonSerializer.Serialize(WhitespaceName.Value,EnumContext.Default.WhitespaceName));
  Observe("invalid-flags-comma",()=>JsonSerializer.Serialize(CommaFlags.Value,EnumContext.Default.CommaFlags));
  PrivateCases.Run();
  Console.WriteLine("PASS source-generated JSON enum field attribute controls completed");
 }
 internal static void Observe(string operation,Func<string?> call)
 {
  try {Console.WriteLine(operation+"|value="+(call()??"<null>"));}
  catch(Exception error){Console.WriteLine(operation+"|error="+error.GetType().FullName+"|hresult="+error.HResult+"|message="+error.Message.Replace('\n',' '));}
 }
}
internal enum WireStatus { DefaultName=0,[JsonStringEnumMemberName("wire-value")]Renamed=1,Alias=1 }
[Flags]internal enum WireFlags { None=0,[JsonStringEnumMemberName("one")]First=1,Second=2 }
internal enum EmptyName { [JsonStringEnumMemberName("")]Value }
internal enum WhitespaceName { [JsonStringEnumMemberName(" bad ")]Value }
[Flags]internal enum CommaFlags { [JsonStringEnumMemberName("bad,name")]Value=1 }
internal sealed class EnumEnvelope { public WireStatus? Value { get;set; } }
[JsonSourceGenerationOptions(GenerationMode=JsonSourceGenerationMode.Metadata,UseStringEnumConverter=true)]
[JsonSerializable(typeof(WireStatus))]
[JsonSerializable(typeof(WireStatus[]))]
[JsonSerializable(typeof(WireFlags))]
[JsonSerializable(typeof(EnumEnvelope))]
[JsonSerializable(typeof(EmptyName))]
[JsonSerializable(typeof(WhitespaceName))]
[JsonSerializable(typeof(CommaFlags))]
internal partial class EnumContext:JsonSerializerContext { }
internal partial class PrivateCases
{
 private enum Hidden { [JsonStringEnumMemberName("private-value")]Value }
 [JsonSourceGenerationOptions(GenerationMode=JsonSourceGenerationMode.Metadata,UseStringEnumConverter=true)]
 [JsonSerializable(typeof(Hidden))]
 private partial class HiddenContext:JsonSerializerContext { }
 internal static void Run()=>Checks.Observe("private-enum",()=>JsonSerializer.Serialize(Hidden.Value,HiddenContext.Default.Hidden));
}
