// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

/// <summary>Isolated inference experiment; never selected by the product DiagnosticSource command.</summary>
internal static class NativeVirtualGetterProbe
{
    internal static void Generate(string manifest, string application, string output)
    {
        var paths = File.ReadAllLines(manifest).Append(application).Where(File.Exists).Select(Path.GetFullPath).Distinct().ToArray();
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(paths), "System.Private.CoreLib");
        var assemblies = paths.Select(context.LoadFromAssemblyPath).ToArray();
        var core = context.CoreAssembly!;
        var dispatch = new NativeVirtualGetterDispatch(core, assemblies);
        var properties = core.GetTypes().Where(type => !type.ContainsGenericParameters && !type.IsByRefLike)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => property.GetMethod is { IsVirtual: true } && property.GetIndexParameters().Length == 0
                && property.PropertyType == core.GetType("System.String")).ToArray();
        var audit = new List<object>();
        foreach (var property in properties.Where(property => property.GetCustomAttributesData().Concat(property.GetMethod!.GetCustomAttributesData())
            .Any(attribute => attribute.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute")))
        {
            foreach (var candidate in assemblies.SelectMany(assembly => assembly.GetTypes())
                .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && property.DeclaringType!.IsAssignableFrom(type)))
            {
                var target = candidate.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == property.GetMethod!.Name && method.GetParameters().Length == 0);
                if (target == null) continue;
                using var stream = File.OpenRead(target.Module.Assembly.Location);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(target.MetadataToken));
                audit.Add(new { property = property.Name, declared = property.DeclaringType!.FullName, receiver = candidate.FullName,
                    target = target.DeclaringType!.FullName, source = target.Module.Assembly.Location, token = target.MetadataToken,
                    il = definition.RelativeVirtualAddress == 0 ? null : Convert.ToHexString(pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes()!),
                    lowered = NativeConstantGetterBody.Infer(core, candidate, target) != null });
            }
        }
        File.WriteAllText(output + ".file-getter-audit.json", JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
        NativePropertyAccessorEmitter.Generate(core, properties, "Neemle.DBAL.VirtualGetterProbe", output, virtualDispatch: dispatch);
    }
}
