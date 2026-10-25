// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;

namespace Neemle.DBAL.Tools;

internal static class NativeAttributeParameters
{
    internal static IEnumerable<ParameterInfo> Enumerate(MemberInfo member)
        => member switch
        {
            MethodInfo method => method.GetParameters().Append(method.ReturnParameter),
            ConstructorInfo constructor => constructor.GetParameters(),
            // A valid accessorless property has no accessor Param rows. MLC's
            // parameter wrapper requires an accessor and rejects these otherwise
            // valid signatures; no attribute declarations can be lost here.
            PropertyInfo property when property.GetMethod == null && property.SetMethod == null => [],
            PropertyInfo property => property.GetIndexParameters(),
            _ => []
        };
}
