// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System.Reflection;
using System.Text;
using Neemle.DBAL.Generators;

namespace Neemle.DBAL.Tools;

/// <summary>Resolves inferred payload shapes against actual emitted metadata without executing application code.</summary>
internal static class NativeDiagnosticPayloadMetadata
{
    internal static Type[] Resolve(MetadataLoadContext context, IEnumerable<Assembly> assemblies)
    {
        var marker = context.CoreAssembly!.GetType("System.Reflection.AssemblyMetadataAttribute", true)!;
        var result = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        foreach (var attribute in assembly.GetCustomAttributesData().Where(attribute => attribute.AttributeType == marker
                     && attribute.ConstructorArguments[0].Value as string == NativeMetadataNames.DiagnosticPayloadBindingsKey))
        foreach (var request in Decode((string)attribute.ConstructorArguments[1].Value!).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var type = ResolveShape(Decode(request));
            result.TryAdd(type.AssemblyQualifiedName!, type);
        }
        return result.Values.ToArray();

        Type Named(string name) => Type.GetType(name, context.LoadFromAssemblyName,
            static (assembly, type, ignoreCase) => assembly?.GetType(type, true, ignoreCase), true)!;
        Type ResolveShape(string description)
        {
            var parts = description.Split('\n');
            switch (parts[0])
            {
                case "N" when parts.Length == 2: return Named(Decode(parts[1]));
                case "V" when parts.Length == 3:
                    var element = ResolveShape(Decode(parts[2]));
                    var rank = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    return rank == 1 ? element.MakeArrayType() : element.MakeArrayType(rank);
                case "G" when parts.Length >= 3:
                    return Named(Decode(parts[1])).MakeGenericType(parts.Skip(2).Select(part => ResolveShape(Decode(part))).ToArray());
                case "A" when parts.Length >= 3:
                    var owner = context.LoadFromAssemblyName(new AssemblyName(Decode(parts[1])));
                    var properties = parts.Skip(2).Select(part => part.Split(':')).ToArray();
                    if (properties.Any(property => property.Length != 2)) throw new InvalidDataException("Invalid inferred anonymous payload schema.");
                    var names = properties.Select(property => Decode(property[0])).ToArray();
                    // Use the emitted compiler template and property signature;
                    // never guess anonymous type ordinals or rewrite CLR names.
                    var candidates = owner.GetTypes().Where(type => type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                        && type.IsGenericTypeDefinition && type.GetGenericArguments().Length == names.Length
                        && type.GetProperties().Select(property => property.Name).SequenceEqual(names)).ToArray();
                    if (candidates.Length != 1) throw new InvalidOperationException("The inferred anonymous payload has no unique emitted template in " + owner.FullName);
                    var definition = candidates[0];
                    var parameters = new Type[names.Length];
                    var actual = definition.GetProperties();
                    for (var index = 0; index < actual.Length; index++)
                    {
                        if (!actual[index].PropertyType.IsGenericParameter) throw new InvalidDataException("The selected anonymous template has an unexpected property signature.");
                        parameters[actual[index].PropertyType.GenericParameterPosition] = ResolveShape(Decode(properties[index][1]));
                    }
                    return definition.MakeGenericType(parameters);
                default: throw new InvalidDataException("Unknown inferred DiagnosticSource payload shape.");
            }
        }
        static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
