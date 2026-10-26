// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Neemle.SystemTextJson
{
    /// <summary>Infrastructure for compiler-inferred, finite JSON operations.</summary>
    public static class InferredJsonOperations
    {
        private static Func<Type, JsonSerializerOptions, JsonTypeInfo?>[] s_resolvers = [];

        /// <summary>Registers a generated metadata factory without executing application code.</summary>
        public static void Register(Func<Type, JsonSerializerOptions, JsonTypeInfo?> resolver)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            Func<Type, JsonSerializerOptions, JsonTypeInfo?>[] current;
            Func<Type, JsonSerializerOptions, JsonTypeInfo?>[] updated;
            do
            {
                current = Volatile.Read(ref s_resolvers);
                updated = new Func<Type, JsonSerializerOptions, JsonTypeInfo?>[current.Length + 1];
                current.CopyTo(updated, 0);
                updated[current.Length] = resolver;
            }
            while (!ReferenceEquals(Interlocked.CompareExchange(ref s_resolvers, updated, current), current));
        }

        internal static bool HasRegistrations => Volatile.Read(ref s_resolvers).Length != 0;

        internal static readonly IJsonTypeInfoResolver Resolver = new InferredResolver();

        private sealed class InferredResolver : IJsonTypeInfoResolver
        {
            public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
            {
                foreach (Func<Type, JsonSerializerOptions, JsonTypeInfo?> resolver in Volatile.Read(ref s_resolvers))
                {
                    if (resolver(type, options) is JsonTypeInfo info)
                    {
                        return info;
                    }
                }

                return null;
            }
        }

        private static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions? options)
        {
            options ??= JsonSerializerOptions.GetInferredDefault();
            options.ConfigureForInferredJsonSerializer();
            return (JsonTypeInfo<T>)options.GetTypeInfoForRootType(typeof(T));
        }

        /// <summary>Executes an inferred, statically bound serialization operation.</summary>
        public static string Serialize<T>(T value, JsonSerializerOptions? options = null)
            => JsonSerializer.Serialize(value, GetTypeInfo<T>(options));

        /// <summary>Executes an inferred, statically bound deserialization operation.</summary>
        public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(json);
            return JsonSerializer.Deserialize(json, GetTypeInfo<T>(options));
        }
    }
}
