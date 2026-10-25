// Copyright (c) Neemle contributors. Licensed under the MIT License.
// Compile this file into a separate prebuilt library for the owner-inference
// fixture. Its private context and private owner must be discovered from selected
// metadata; referencing the marker is not permission to execute the constructor.
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Neemle.DBAL.Conformance.Json.AttributeOwners.Prebuilt;

public sealed class LibraryMarker
{
    public LibraryMarker() => throw new InvalidOperationException("Metadata-only library must not execute.");

    [JsonSerializable(typeof(PrivateOwner))]
    private sealed class PrivateContext : JsonSerializerContext
    {
        private JsonTypeInfo<ClosedOwner<long>[]> Closed => throw new InvalidOperationException();
        public PrivateContext() : base(null) => throw new InvalidOperationException();
        protected override JsonSerializerOptions? GeneratedSerializerOptions => throw new InvalidOperationException();
        public override JsonTypeInfo? GetTypeInfo(Type type) => throw new InvalidOperationException();
    }

    private sealed class PrivateOwner;
    private sealed class ClosedOwner<T>;
}
