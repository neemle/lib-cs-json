// Copyright (c) Neemle contributors. Licensed under the MIT License.
#nullable enable
using System.Reflection;
using System.Text.Json.Serialization;

namespace Neemle.DBAL.Runtime;

// Keep the finite attribute contract outside the open EnumConverter<T> owner.
internal static class NativeJsonEnumAttributeQueries
{
    internal static JsonStringEnumMemberNameAttribute? ReadName(FieldInfo field)
        => NativeAttributeQueries.ReadAttribute<JsonStringEnumMemberNameAttribute>(field, true);
}
