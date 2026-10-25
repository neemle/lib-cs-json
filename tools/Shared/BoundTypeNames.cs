// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Neemle.DBAL.Generators;

internal static class BoundTypeNames
{
    internal static string? ReflectionName(ITypeSymbol type, bool definitionOnly = false)
    {
        if (type is IArrayTypeSymbol array)
        {
            var element = ReflectionName(array.ElementType);
            if (element is null)
            {
                return null;
            }

            var separator = AssemblySeparator(element);
            return element.Substring(0, separator) + "[" + new string(',', array.Rank - 1) + "]" + element.Substring(separator);
        }

        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error
            || (!definitionOnly && named.IsUnboundGenericType) || named.IsRefLikeType || named.IsAnonymousType)
        {
            return null;
        }

        if (named.IsTupleType)
        {
            named = named.TupleUnderlyingType ?? named;
        }

        var arguments = definitionOnly ? Array.Empty<string?>() : AllArguments(named).Select(argument => ReflectionName(argument)).ToArray();
        if (arguments.Any(argument => argument is null))
        {
            return null;
        }

        var names = new Stack<string>();
        for (var current = named; current != null; current = current.ContainingType)
        {
            // Method groups inside nameof can have a nameless semantic type.
            // Such compiler symbols have no CLR type to resolve or root.
            if (current.MetadataName.Length == 0)
            {
                return null;
            }
            names.Push(current.MetadataName);
        }

        var prefix = named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace.ToDisplayString() + ".";
        var name = prefix + string.Join("+", names);
        if (arguments.Length != 0)
        {
            name += "[[" + string.Join("],[", arguments) + "]]";
        }

        return name + "," + named.ContainingAssembly.Identity.Name;
    }

    internal static IEnumerable<ITypeSymbol> AllArguments(INamedTypeSymbol type)
        => (type.ContainingType == null ? Enumerable.Empty<ITypeSymbol>() : AllArguments(type.ContainingType))
            .Concat(type.TypeArguments);

    private static int AssemblySeparator(string name)
    {
        var depth = 0;
        for (var index = 0; index < name.Length; index++)
        {
            depth += name[index] == '[' ? 1 : name[index] == ']' ? -1 : 0;
            if (depth == 0 && name[index] == ',')
            {
                return index;
            }
        }

        throw new InvalidOperationException("Invalid reflected type name.");
    }
}
