// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text;

namespace Neemle.DBAL.Tools;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--assembly-types-probe", var assemblyManifest, var assemblyApplication, var assemblyOutput])
        {
            try { NativeAssemblyTypesProbe.Generate(assemblyManifest, assemblyApplication, assemblyOutput); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL assembly type experiment failed: {error}"); return 1; }
        }
        if (args is ["--verify-native-type-references", var referenceManifest, var generatedAssembly])
        {
            try
            {
                using var metadata = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(File.ReadAllLines(referenceManifest)), "System.Private.CoreLib");
                NativeEmissionTypeNames.ValidateReferences(generatedAssembly, metadata);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine($"DBAL generated type-reference proof failed: {error}"); return 1; }
        }
        if (args is ["--type-metadata-probe-universe", var recipeManifest, var recipeApplication, var recipeUniverse, var recipeOutput])
        {
            try { NativeTypeMetadataProbe.Generate(recipeManifest, recipeApplication, recipeOutput, true, false, recipeUniverse); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL diagnostic recipe experiment failed: {error}"); return 1; }
        }
        if (args is ["--diagnostic-metadata-universe", var universeManifest, var universeApplication, var universeOutput])
        {
            try { NativeDiagnosticMetadataUniverse.Generate(universeManifest, universeApplication, universeOutput); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL diagnostic metadata inference failed: {error}"); return 1; }
        }
        if (args is [var metadataCommand, var metadataManifest, var metadataApplication, var metadataOutput]
            && metadataCommand is "--type-metadata-probe" or "--type-metadata-probe-core" or "--type-metadata-probe-core-all")
        {
            try { NativeTypeMetadataProbe.Generate(metadataManifest, metadataApplication, metadataOutput,
                metadataCommand != "--type-metadata-probe", metadataCommand == "--type-metadata-probe-core-all"); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL metadata handle experiment failed: {error}"); return 1; }
        }
        if (args is ["--virtual-getter-probe", var probeManifest, var probeApplication, var probeOutput])
        {
            try { NativeVirtualGetterProbe.Generate(probeManifest, probeApplication, probeOutput); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL virtual getter experiment failed: {error}"); return 1; }
        }
        if (args is ["--oracle-constructors", var oracleManifest, var oracleApplication, var oracleOutput])
        {
            try { NativeConstructorBridge.Generate(oracleManifest, oracleApplication, oracleOutput, oracle: true); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL Oracle constructor generation failed: {error}"); return 1; }
        }
        if (args is ["--constructor-bridge", var constructorManifest, var constructorApplication, var constructorOutput])
        {
            try { NativeConstructorBridge.Generate(constructorManifest, constructorApplication, constructorOutput); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL constructor operation generation failed: {error}"); return 1; }
        }
        if (args is ["--configuration-constructors", var configurationManifest, var configurationApplication, var configurationOutput])
        {
            try { NativeConstructorBridge.Generate(configurationManifest, configurationApplication, configurationOutput, configuration: true); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL configuration constructor generation failed: {error}"); return 1; }
        }
        if (args is ["--diagnostic-bridge", var manifest, var application, var bridge])
        {
            try { NativeDiagnosticAccessorBridge.Generate(manifest, application, bridge); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL DiagnosticSource accessor generation failed: {error}"); return 1; }
        }
        if (args is ["--assembly-load-errors", var resources, var destination])
        {
            try { NativeAssemblyLoadErrorMetadata.Generate(resources, destination); return 0; }
            catch (Exception error) { Console.Error.WriteLine($"DBAL loader error inference failed: {error}"); return 1; }
        }
        if (args is ["--json-scalars", var references, var output])
        {
            try
            {
                NativeJsonScalarMetadata.Generate(references, output);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"DBAL scalar JSON metadata generation failed: {error}");
                return 1;
            }
        }
        if (args.Length is 7 or 8 && args[0] is "--query-bridge" or "--json-bridge")
        {
            try
            {
                NativeQueryBridge.Generate(args[1], args[2], args[3], args[4], args[5], args[6], args.Length == 8 ? args[7] : null,
                    jsonOnly: args[0] == "--json-bridge");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"DBAL query bridge generation failed: {error}");
                return 1;
            }
        }
        if (args.Length == 4 && args[0] == "--query-iterators")
        {
            try
            {
                NativeQueryIteratorMetadata.Generate(args[1], args[2], args[3]);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"DBAL iterator metadata generation failed: {error}");
                return 1;
            }
        }
        if (args.Length == 5 && args[0] == "--model-bridge")
        {
            try
            {
                NativeModelBridge.Generate(args[1], args[2], args[3], args[4]);
                return 0;
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(args[4]);
                File.WriteAllText(Path.Combine(args[4], "generation-error.txt"), error.ToString());
                Console.Error.WriteLine($"DBAL private model bridge generation failed: {error}");
                return 1;
            }
        }

        if (args.Length == 4 && args[0] == "--parameter-metadata")
        {
            try
            {
                NativeParameterMetadata.Generate(args[1], args[2], args[3]);
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(args[3] + ".error.txt", error.ToString());
                Console.Error.WriteLine($"DBAL parameter metadata generation failed: {error}");
                return 1;
            }
        }

        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Neemle.DBAL.RuntimeContracts <assembly-manifest> <output.cs>");
            return 2;
        }

        try
        {
            Generate(args[0], args[1]);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"DBAL runtime contract generation failed: {exception.Message}");
            return 1;
        }
    }

    private static void Generate(string manifest, string output)
    {
        var references = File.ReadAllLines(manifest)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (references.Length == 0 || references.Any(path => !File.Exists(path)))
        {
            throw new InvalidOperationException("The target assembly manifest is empty or contains missing files.");
        }

        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(references), "System.Private.CoreLib");
        var legacyEnumerable = context.CoreAssembly!.GetType(typeof(System.Collections.IEnumerable).FullName!, throwOnError: true)!;
        var enumerable = context.CoreAssembly.GetType(typeof(IEnumerable<>).FullName!, throwOnError: true)!;
        var asyncEnumerable = context.CoreAssembly.GetType(typeof(IAsyncEnumerable<>).FullName!, throwOnError: true)!;
        var types = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var fingerprints = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in references)
        {
            var assembly = context.LoadFromAssemblyPath(path);
            var contracts = assembly.GetTypes().Where(type => type.GetInterfaces().Any(contract =>
                    contract == legacyEnumerable
                    || contract.IsConstructedGenericType
                    && (contract.GetGenericTypeDefinition() == enumerable || contract.GetGenericTypeDefinition() == asyncEnumerable)))
                .ToArray();
            if (contracts.Length == 0)
            {
                continue;
            }

            foreach (var type in contracts)
            {
                types.Add(type.AssemblyQualifiedName
                    ?? throw new InvalidOperationException($"The target sequence type '{type}' has no CLR name."), type);
            }

            fingerprints[assembly.FullName ?? throw new InvalidOperationException($"The target assembly '{path}' has no identity.")]
                = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }

        var source = new StringBuilder("// <auto-generated />\n#nullable enable\n");
        foreach (var fingerprint in fingerprints)
        {
            source.Append("// ").Append(fingerprint.Key).Append(": ").AppendLine(fingerprint.Value);
        }

        source.AppendLine("using Contract = global::Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes.RuntimeContract;");
        source.AppendLine("using Pattern = global::Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes.RuntimeTypePattern;");
        source.AppendLine("namespace Neemle.DBAL.Generated.NativeRuntimeContracts;");
        source.AppendLine("internal static class RuntimeSequenceContracts");
        source.AppendLine("{");
        source.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("    internal static void Initialize()");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;");
        source.Append("        global::Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes.RegisterInterfaceAmbiguityFormat(")
            .Append(Literal(ReadRuntimeString(context.CoreAssembly.Location, "Arg_AmbiguousMatchException_MemberInfo")))
            .AppendLine(");");
        const int contractsPerMethod = 64;
        var batches = types.Chunk(contractsPerMethod).ToArray();
        for (var i = 0; i < batches.Length; i++)
        {
            source.Append("        Register").Append(i).AppendLine("();");
        }

        source.AppendLine("    }");
        for (var i = 0; i < batches.Length; i++)
        {
            source.Append("    private static void Register").Append(i).AppendLine("()");
            source.AppendLine("    {");
            foreach (var entry in batches[i])
            {
                source.Append("        global::Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes.RegisterRuntimeType(")
                    .Append(Literal(entry.Key));
                foreach (var contract in entry.Value.GetInterfaces().Concat(BaseTypes(entry.Value)).Append(entry.Value)
                             .Where(type => type.IsGenericType || type.IsInterface))
                {
                    var definition = contract.IsGenericType ? contract.GetGenericTypeDefinition() : contract;
                    source.Append(",\n            new Contract(").Append(Literal(definition.AssemblyQualifiedName!));
                    foreach (var argument in contract.GetGenericArguments())
                    {
                        source.Append(", ").Append(PatternExpression(argument));
                    }

                    source.Append(')');
                    if (contract.IsInterface && contract != entry.Value)
                    {
                        source.Append(" { ImplementedInterfaceName = ").Append(Literal(contract.Name))
                            .Append(", InterfaceTypeName = ").Append(Literal(definition.FullName!));
                        if (definition.DeclaringType is { } declaringType)
                        {
                            source.Append(", InterfaceDeclaringTypeName = ").Append(Literal(declaringType.ToString()));
                        }
                        source.Append(" }");
                    }
                }

                source.AppendLine(");");
            }

            source.AppendLine("    }");
        }

        source.AppendLine("}");
        var content = source.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (!File.Exists(output) || File.ReadAllText(output) != content)
        {
            File.WriteAllText(output, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Console.WriteLine($"DBAL inferred {types.Count} runtime sequence contracts from {references.Length} target assemblies.");
    }

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        while (type.BaseType is { } parent)
        {
            yield return parent;
            type = parent;
        }
    }

    private static string PatternExpression(Type type)
        => type.IsGenericParameter ? $"Pattern.Parameter({type.GenericParameterPosition})"
            : type.IsArray
                ? $"Pattern.Array({PatternExpression(type.GetElementType()!)}, {type.GetArrayRank()}, {type.IsSZArray.ToString().ToLowerInvariant()})"
            : type.IsConstructedGenericType
                ? "Pattern.Constructed(" + Literal(type.GetGenericTypeDefinition().AssemblyQualifiedName!) + ", "
                    + string.Join(", ", type.GetGenericArguments().Select(PatternExpression)) + ").WithReflectionName("
                    + Literal(type.GetGenericTypeDefinition().FullName!) + ")"
                : "Pattern.Named(" + Literal(type.AssemblyQualifiedName!) + ").WithReflectionName(" + Literal(type.ToString()) + ")";

    private static string ReadRuntimeString(string coreLibrary, string name)
    {
        using var stream = File.OpenRead(coreLibrary);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil
                || metadata.GetString(resource.Name) != "System.Private.CoreLib.Strings.resources")
            {
                continue;
            }

            var block = image.GetSectionData(image.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
            var reader = block.GetReader(checked((int)resource.Offset), block.Length - checked((int)resource.Offset));
            var length = reader.ReadInt32();
            using var resources = new ResourceReader(new MemoryStream(reader.ReadBytes(length), writable: false));
            foreach (System.Collections.DictionaryEntry entry in resources)
            {
                if ((string)entry.Key == name && entry.Value is string value)
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException($"The target CoreLib '{coreLibrary}' has no required runtime diagnostic resource '{name}'.");
    }

    private static string Literal(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '\\' or '\"')
            {
                result.Append('\\').Append(character);
            }
            else if (char.IsControl(character))
            {
                result.Append("\\u").Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(character);
            }
        }

        return result.Append('"').ToString();
    }
}
