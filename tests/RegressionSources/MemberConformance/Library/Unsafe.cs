// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Diagnostics.CodeAnalysis;
namespace Neemle.DBAL.JsonMemberConformance;
[AttributeUsage(AttributeTargets.Class)]
public sealed class UnsafeProbeAttribute : Attribute
{
    [RequiresUnreferencedCode("genuine serialized attribute constructor")]
    [RequiresDynamicCode("genuine serialized attribute constructor")]
    public UnsafeProbeAttribute() { }
}
[UnsafeProbe]
public sealed class UnrelatedUnsafe { public string? Unused { get; set; } }
