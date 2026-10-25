// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Neemle.DBAL.JsonRawRoles;
foreach (var type in new[] { typeof(EnumOwner), typeof(CollectionOwner), typeof(GuardedDerived), typeof(ConvertedOwner), typeof(JsonRequiredOwner), typeof(IgnoredDerived), typeof(MemberBoundaries), typeof(InvalidIgnoredOwner) })
{
    try { Console.WriteLine(type.Name + ":" + new DefaultJsonTypeInfoResolver().GetTypeInfo(type, new JsonSerializerOptions()).Kind); }
    catch (InvalidOperationException error) when (type == typeof(IgnoredDerived)) { Console.WriteLine(type.Name + ":" + error.GetType().Name); }
}
Console.WriteLine("unused:" + UnusedTypeAttribute.Count + ":" + UnusedPropertyAttribute.Count);
new DefaultJsonTypeInfoResolver().GetTypeInfo(typeof(ActualCollectionType), new JsonSerializerOptions());
Console.WriteLine("actualType:" + ActualReadAttribute.Count);
new DefaultJsonTypeInfoResolver().GetTypeInfo(typeof(ActualRequiredProperty), new JsonSerializerOptions());
Console.WriteLine("actualMember:" + ActualReadAttribute.Count);
[JsonSerializable(typeof(EnumOwner)), JsonSerializable(typeof(CollectionOwner)), JsonSerializable(typeof(GuardedDerived)), JsonSerializable(typeof(ConvertedOwner)), JsonSerializable(typeof(JsonRequiredOwner)), JsonSerializable(typeof(IgnoredDerived)), JsonSerializable(typeof(MemberBoundaries)), JsonSerializable(typeof(InvalidIgnoredOwner)), JsonSerializable(typeof(ActualCollectionType)), JsonSerializable(typeof(ActualRequiredProperty))]
internal partial class RawRoleContext : JsonSerializerContext { }
