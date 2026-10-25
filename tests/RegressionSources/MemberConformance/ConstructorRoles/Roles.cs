// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Text.Json.Serialization;
namespace Neemle.DBAL.JsonMemberConformance;
public sealed class PublicDefault { public PublicDefault() { } public PublicDefault(int value) { } }
public sealed class LonePublic { public LonePublic(int value) { } }
public sealed class PrivateAnnotated { public PrivateAnnotated() { } [JsonConstructor] private PrivateAnnotated(int value) { } }
public sealed class MultipleAnnotated { [JsonConstructor] public MultipleAnnotated() { } [JsonConstructor] public MultipleAnnotated(int value) { } }
public sealed class NoPublic { private NoPublic(int value) { } }
public struct AnnotatedStruct { [JsonConstructor] public AnnotatedStruct(int value) { Value = value; } public int Value { get; set; } }
public struct ExplicitDefaultStruct { public ExplicitDefaultStruct() { Value = 1; } public int Value { get; set; } }
public abstract class AbstractRoot { public AbstractRoot() { } }
public interface InterfaceRoot { }
public sealed class EnumerableRoot : System.Collections.Generic.List<int> { public EnumerableRoot() { } }
