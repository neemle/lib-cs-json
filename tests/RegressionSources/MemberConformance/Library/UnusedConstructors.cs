// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Diagnostics.CodeAnalysis;
namespace Neemle.DBAL.JsonMemberConformance;
[AttributeUsage(AttributeTargets.Constructor)]
public sealed class UnsafeConstructorAttribute : Attribute
{
    [RequiresUnreferencedCode("unused JSON constructor attributes must remain inert")]
    [RequiresDynamicCode("unused JSON constructor attributes must remain inert")]
    public UnsafeConstructorAttribute() { }
}
public struct UnannotatedStruct
{
    [UnsafeConstructor] public UnannotatedStruct(int value) { Value = value; }
    public int Value { get; set; }
}
public class UnadvertisedBase
{
    [UnsafeConstructor] public UnadvertisedBase() { }
    public string? Name { get; set; }
}
public sealed class AdvertisedDerived : UnadvertisedBase;
