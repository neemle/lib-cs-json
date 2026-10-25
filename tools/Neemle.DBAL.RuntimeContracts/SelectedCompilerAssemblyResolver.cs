// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Security.Cryptography;

namespace Neemle.DBAL.Tools;

/// <summary>Resolves metadata against the implementation files already selected for compilation.</summary>
internal sealed class SelectedCompilerAssemblyResolver : MetadataAssemblyResolver
{
    private readonly Dictionary<string, Input> _inputs = new(StringComparer.OrdinalIgnoreCase);

    internal SelectedCompilerAssemblyResolver(IEnumerable<string> paths)
    {
        // Validate every supplied image eagerly, including unreachable optional inputs.
        // A malformed compiler manifest must not appear to work because traversal skipped it.
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var identity = AssemblyName.GetAssemblyName(path);
            var name = identity.Name ?? throw new BadImageFormatException("Missing assembly name: " + path);
            var input = new Input(identity, path);
            if (!_inputs.TryGetValue(name, out var previous))
            {
                _inputs.Add(name, input);
                continue;
            }

            // Duplicate aliases of the identical selected image are harmless. Two distinct
            // implementations of one simple name are an ambiguous compiler universe.
            if (!string.Equals(previous.Identity.FullName, identity.FullName, StringComparison.OrdinalIgnoreCase)
                || !Fingerprint(previous.Path).AsSpan().SequenceEqual(Fingerprint(path)))
                throw new InvalidOperationException($"Ambiguous selected compiler assembly '{name}': '{previous.Path}' and '{path}'.");
        }
    }

    public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name || !_inputs.TryGetValue(name, out var input)) return null;
        if (!string.Equals(input.Identity.CultureName ?? "", assemblyName.CultureName ?? "", StringComparison.OrdinalIgnoreCase))
            return null;
        if (assemblyName.Version is { } requested && input.Identity.Version is { } selected && selected < requested)
            throw new InvalidOperationException($"Selected compiler assembly '{input.Identity.FullName}' is older than requested '{assemblyName.FullName}'.");

        // CLR implementation selection permits an upward version with a different key.
        // PathAssemblyResolver applies a different key filter; loading the exact selected
        // PE here matches the validated input choice without executing application code.
        return context.LoadFromAssemblyPath(input.Path);
    }

    private static byte[] Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private sealed record Input(AssemblyName Identity, string Path);
}
