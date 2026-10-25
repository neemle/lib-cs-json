// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Neemle.DBAL.JsonRawRoles;
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum, Inherited = false)]
public sealed class UnusedTypeAttribute : Attribute
{
    public static int Count;
    [RequiresUnreferencedCode("unused JSON type role")]
    [RequiresDynamicCode("unused JSON type role")]
    public UnusedTypeAttribute() { Count++; }
}
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class UnusedPropertyAttribute : Attribute
{
    public static int Count;
    [RequiresUnreferencedCode("unused JSON member role")]
    [RequiresDynamicCode("unused JSON member role")]
    public UnusedPropertyAttribute() { Count++; }
}
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class ActualReadAttribute : Attribute
{
    public static int Count;
    [RequiresUnreferencedCode("genuine JSON eager raw role")]
    [RequiresDynamicCode("genuine JSON eager raw role")]
    public ActualReadAttribute() { Count++; }
}
[UnusedType] public enum EnumOwner { One }
public sealed class CollectionOwner : List<int>
{
    [SetsRequiredMembers] public CollectionOwner() { Ignored = ""; }
    [UnusedProperty] public required string Ignored { get; init; }
}
[UnusedType] public class UnusedBase { public int Value { get; set; } }
public sealed class GuardedDerived : UnusedBase { [SetsRequiredMembers] public GuardedDerived() { } }
[UnusedType, JsonConverter(typeof(CustomConverter))] public sealed class ConvertedOwner
{
    [UnusedProperty] public required string Value { get; set; }
}
public sealed class CustomConverter : JsonConverter<ConvertedOwner>
{
    public override ConvertedOwner? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new() { Value = "" };
    public override void Write(Utf8JsonWriter writer, ConvertedOwner value, JsonSerializerOptions options) => writer.WriteNullValue();
}
public sealed class JsonRequiredOwner
{
    [UnusedProperty, JsonRequired] public required string Value { get; set; }
}
public class IgnoredBase { [UnusedProperty] public virtual required string Value { get; set; } }
public sealed class IgnoredDerived : IgnoredBase { [JsonIgnore] public override required string Value { get; set; } }
public sealed class MemberBoundaries
{
    public required string Value { get; set; }
    [UnusedProperty] private string Private { get; set; } = "";
    [UnusedProperty] public static string Static { get; set; } = "";
    [UnusedProperty] public string this[int index] => "";
}
public unsafe sealed class InvalidIgnoredOwner
{
    public required string Value { get; set; }
    [UnusedProperty, JsonIgnore] public int* Pointer { get; set; }
}
[ActualRead] public sealed class ActualCollectionType : List<int> { }
public sealed class ActualRequiredProperty
{
    [ActualRead] public required string Value { get; set; }
}
