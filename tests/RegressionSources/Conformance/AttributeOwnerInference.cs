// Copyright (c) Neemle contributors. Licensed under the MIT License.
// Metadata fixture: compile with a plain Roslyn CSharpCompilation, without
// running source generators. The native tool must infer the canonical contracts
// below without loading this assembly or invoking any context/member body.
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Neemle.DBAL.Conformance.Json.AttributeOwners
{
    internal static class SelectedLibraryReference
    {
        internal static Type Marker => typeof(Prebuilt.LibraryMarker);
    }

    // Positive owners: AttributeOnly, FieldOnly, PropertyOnly, ReturnOnly,
    // ParameterOnly, PrivateBox<int>[], and ClosedInherited[] from the actual
    // closed generic context base. These are test expectations, not a registry.
    [JsonSerializable(typeof(AttributeOnly))]
    [JsonSerializable(typeof(UnusedEnvelope<UnusedArgument>))]
    [JsonSerializable(typeof(DeclaredEnum))]
    internal sealed class CanonicalContext : ContextBase
    {
        private JsonTypeInfo<FieldOnly> _field = null!;
        internal JsonTypeInfo<PropertyOnly> Property => throw NeverExecute();
        private JsonTypeInfo<ReturnOnly> Return() => throw NeverExecute();
        private void Parameter(JsonTypeInfo<ParameterOnly> value) => throw NeverExecute();
        private JsonTypeInfo<PrivateBox<int>[]> ClosedArray => throw NeverExecute();
        private JsonTypeInfo<FieldEnum> EnumContract => throw NeverExecute();
        private JsonTypeInfo<FieldOnly> ReadField() => _field;

        // A member's unrelated result Type is not a JSON serialization contract.
        private NonJsonSignature Unrelated() => throw NeverExecute();
        private Lookalikes.JsonTypeInfo<FakeInfoOwner> FakeInfo => throw NeverExecute();
        private static Exception NeverExecute() => new InvalidOperationException("Metadata-only context must not execute.");
        private sealed class PrivateBox<T>;
    }

    internal abstract class GenericContext<T> : ContextBase
    {
        internal JsonTypeInfo<T[]> Inherited => throw new InvalidOperationException("Do not execute metadata fixtures.");
    }

    internal sealed class ClosedContext : GenericContext<ClosedInherited>;

    // A fake attribute on an actual context must not become a Type owner.
    [Lookalikes.JsonSerializable(typeof(FakeAttributeOwner))]
    internal sealed class FakeAttributeContext : ContextBase;

    // Canonical attributes/signatures on a non-context declaration are unrelated.
    [JsonSerializable(typeof(UnrelatedAttributeOwner))]
    internal sealed class UnrelatedDeclaration
    {
        internal JsonTypeInfo<UnrelatedSignatureOwner> Value => throw new InvalidOperationException();
    }

    // A lookalike context base cannot opt in by short names alone.
    [Lookalikes.JsonSerializable(typeof(FakeContextOwner))]
    internal sealed class FakeContext : Lookalikes.JsonSerializerContext
    {
        internal JsonTypeInfo<FakeContextSignatureOwner> Value => throw new InvalidOperationException();
    }

    internal abstract class ContextBase : JsonSerializerContext
    {
        protected ContextBase() : base(null) => throw new InvalidOperationException("A metadata reader executed a context constructor.");
        protected override JsonSerializerOptions? GeneratedSerializerOptions => throw new InvalidOperationException();
        public override JsonTypeInfo? GetTypeInfo(Type type) => throw new InvalidOperationException();
    }

    internal sealed class AttributeOnly;
    internal sealed class FieldOnly;
    internal sealed class PropertyOnly;
    internal sealed class ReturnOnly;
    internal sealed class ParameterOnly;
    internal enum DeclaredEnum { [JsonStringEnumMemberName("declared")] Value }
    internal enum FieldEnum { [JsonStringEnumMemberName("field")] Value }
    internal enum UnrelatedEnum { [JsonStringEnumMemberName("unrelated")] Value }
    internal sealed class ClosedInherited;
    internal sealed class NonJsonSignature;
    internal sealed class FakeInfoOwner;
    internal sealed class FakeAttributeOwner;
    internal sealed class UnrelatedAttributeOwner;
    internal sealed class UnrelatedSignatureOwner;
    internal sealed class FakeContextOwner;
    internal sealed class FakeContextSignatureOwner;
    internal sealed class UnusedEnvelope<T>
    {
        public string Value { get; set; } = "value";
    }
    [JsonPolymorphic]
    internal sealed class UnusedArgument
    {
        static UnusedArgument() => throw new InvalidOperationException("An unused CLR type argument must not execute.");
    }
}

namespace Neemle.DBAL.Conformance.Json.AttributeOwners.Lookalikes
{
    internal sealed class JsonTypeInfo<T>;
    internal abstract class JsonSerializerContext;

    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class JsonSerializableAttribute : Attribute
    {
        public JsonSerializableAttribute(Type type) => throw new InvalidOperationException("A metadata reader constructed a lookalike attribute.");
    }
}

namespace Fixture
{
    // The maintained test reads these Constant rows through metadata only.
    public static class Expectations
    {
        public const string Included =
            "Neemle.DBAL.Conformance.Json.AttributeOwners.AttributeOnly\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.FieldOnly\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.PropertyOnly\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.ReturnOnly\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.ParameterOnly\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.DeclaredEnum\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.FieldEnum\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.CanonicalContext+PrivateBox`1[[System.Int32, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]][]\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.ClosedInherited[]\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.Prebuilt.LibraryMarker+PrivateOwner\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.Prebuilt.LibraryMarker+ClosedOwner`1[[System.Int64, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]][]";

        public const string Excluded =
            "Neemle.DBAL.Conformance.Json.AttributeOwners.NonJsonSignature\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.FakeInfoOwner\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.FakeAttributeOwner\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.UnrelatedAttributeOwner\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.UnrelatedSignatureOwner\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.FakeContextOwner\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.FakeContextSignatureOwner\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.UnrelatedEnum\n" +
            "Neemle.DBAL.Conformance.Json.AttributeOwners.UnusedArgument";
    }
}
