// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
try {
var root = Path.GetFullPath(args[0]);
var manifest = File.ReadAllLines(args[1]).Where(line => !string.IsNullOrWhiteSpace(line)).Distinct().ToArray();
var frozen = Assembly.LoadFrom(args[2]);
using var context = new MetadataLoadContext(new PathAssemblyResolver(manifest), "System.Private.CoreLib");
var json = context.LoadFromAssemblyPath(manifest.Single(path => Path.GetFileName(path) == "System.Text.Json.dll"));
var policyType = frozen.GetType("Neemle.DBAL.Tools.NativeJsonConstructorMetadata", true)!;
var policy = Activator.CreateInstance(policyType, BindingFlags.Instance | BindingFlags.NonPublic, null, [context, json], null)!;
var select = policyType.GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Instance)!;
var rolePath = manifest.Single(path => Path.GetFileName(path) == "Neemle.DBAL.JsonConstructor.Roles.dll");
var fsPath = manifest.Single(path => Path.GetFileName(path) == "Neemle.DBAL.JsonConstructor.FSharp.Library.dll");
var roles = Assembly.LoadFrom(rolePath); var fsharp = Assembly.LoadFrom(fsPath);
var types = new[] { "UnannotatedStruct", "AdvertisedDerived", "UnadvertisedBase", "PublicDefault", "LonePublic", "PrivateAnnotated", "MultipleAnnotated", "NoPublic", "AnnotatedStruct", "ExplicitDefaultStruct", "AbstractRoot", "InterfaceRoot", "EnumerableRoot", "UnknownMappingStruct" }
    .Select(name => roles.GetType("Neemle.DBAL.JsonMemberConformance." + name, true)!)
    .Concat([fsharp.GetType("Neemle.DBAL.JsonMemberConformance.RealRecord", true)!, fsharp.GetType("Neemle.DBAL.JsonMemberConformance.RealUnion", true)!, fsharp.GetType("Neemle.DBAL.JsonMemberConformance.RealStructUnion", true)!, fsharp.GetType("Neemle.DBAL.JsonMemberConformance.RealObject", true)!, typeof(Microsoft.FSharp.Core.FSharpOption<int>), typeof(Microsoft.FSharp.Core.FSharpValueOption<int>), typeof(Microsoft.FSharp.Collections.FSharpList<int>), typeof(KeyValuePair<string, int>), typeof(Version), typeof(List<int>), typeof(JsonElement)]);
var rows = new List<object>();
foreach (var actual in types)
{
    var metadata = actual.Assembly == typeof(object).Assembly
        ? context.CoreAssembly!.GetType(actual.IsConstructedGenericType ? actual.GetGenericTypeDefinition().FullName! : actual.FullName!, true)!
        : context.LoadFromAssemblyName(actual.Assembly.GetName()).GetType(actual.IsConstructedGenericType ? actual.GetGenericTypeDefinition().FullName! : actual.FullName!, true)!;
    if (actual.IsConstructedGenericType) metadata = metadata.MakeGenericType(actual.GetGenericArguments().Select(argument => context.CoreAssembly!.GetType(argument.FullName!, true)!).ToArray());
    var selected = select.Invoke(policy, [metadata])!;
    var selectedCtor = (ConstructorInfo?)selected.GetType().GetProperty("Constructor")!.GetValue(selected);
    var reason = (string)selected.GetType().GetProperty("Reason")!.GetValue(selected)!;
    var unsupported = (bool)selected.GetType().GetProperty("Unsupported")!.GetValue(selected)!;
    string? oracle = null; string? error = null;
    if (actual.Name == "UnknownMappingStruct")
    {
        if (!unsupported || selectedCtor != null) throw new Exception("Computed FSharp flags were accepted as a constructor proof.");
    }
    else
    {
        try
        {
            var resolver = new DefaultJsonTypeInfoResolver(); var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
            oracle = Signature(resolver.GetTypeInfo(actual, options).ConstructorAttributeProvider as ConstructorInfo);
        }
        catch (InvalidOperationException failure) when (actual.Name == "MultipleAnnotated") { error = failure.GetType().Name + ":" + failure.Message; }
        if (unsupported || Signature(selectedCtor) != oracle) throw new Exception(actual + " selection mismatch: " + Signature(selectedCtor) + " vs " + oracle + " / " + reason);
    }
    rows.Add(new { owner = actual.AssemblyQualifiedName, constructor = Signature(selectedCtor), oracle, error, reason, unsupported });
}
File.WriteAllText(args[3], JsonSerializer.Serialize(new { passed = true, rows, applicationCodeExecutedByInference = false, metadataOnlySelector = true, oracle = "Actual stock JIT DefaultJsonTypeInfoResolver.GetTypeInfo(...).ConstructorAttributeProvider" }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Constructor role controls PASS: " + rows.Count);
return 0;
} catch (Exception error) { Console.Error.WriteLine(error); return 1; }
static string? Signature(ConstructorInfo? constructor) => constructor == null ? null : constructor.DeclaringType!.FullName + "(" + string.Join(",", constructor.GetParameters().Select(parameter => parameter.ParameterType.FullName)) + ")";
