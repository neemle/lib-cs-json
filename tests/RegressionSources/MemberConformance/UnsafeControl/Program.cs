// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neemle.DBAL.JsonMemberConformance;
internal static partial class Program
{
    private static void Main()
    {
#if UNSAFE_JSON_OWNER
        Console.WriteLine(Unique<UnsafeProbeAttribute>(null, typeof(UnrelatedUnsafe), false) != null);
#else
        Console.WriteLine(Unique<JsonConverterAttribute>(null, typeof(Envelope<UnrelatedUnsafe>), false) == null);
        Console.WriteLine(JsonSerializer.Serialize(new Envelope<UnrelatedUnsafe> { Name = "kept" }, Context.Default.EnvelopeUnrelatedUnsafe));
#endif
    }
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GetUniqueCustomAttribute")]
    private static extern TAttribute? Unique<TAttribute>([UnsafeAccessorType("System.Text.Json.Reflection.ReflectionExtensions,System.Text.Json")] object? target, MemberInfo element, bool inherit) where TAttribute : Attribute;
    private sealed class Envelope<T> { public string? Name { get; set; } }
#if UNSAFE_JSON_OWNER
    [JsonSerializable(typeof(UnrelatedUnsafe))]
#else
    [JsonSerializable(typeof(Envelope<UnrelatedUnsafe>))]
#endif
    private partial class Context : JsonSerializerContext;
}
