// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neemle.DBAL.Runtime.Build;

internal static class Program
{
    private static readonly Guid EmbeddedSource = new("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static void Main(string[] args)
    {
        if (args is ["--forwarders", var forwarderInput, var forwarderOutput])
        {
            using var input = File.OpenRead(forwarderInput);
            using var forwarderImage = new PEReader(input);
            var forwarderMetadata = forwarderImage.GetMetadataReader();
            var forwarders = new List<object>();
            foreach (var handle in forwarderMetadata.ExportedTypes)
            {
                var type = forwarderMetadata.GetExportedType(handle);
                if (!type.IsForwarder) continue;
                if (type.Implementation.Kind != HandleKind.AssemblyReference)
                    throw new BadImageFormatException("Unrepresented nested forwarded type");
                var owner = forwarderMetadata.GetAssemblyReference((AssemblyReferenceHandle)type.Implementation);
                forwarders.Add(new { @namespace = forwarderMetadata.GetString(type.Namespace), name = forwarderMetadata.GetString(type.Name),
                    assembly = forwarderMetadata.GetString(owner.Name), assemblyVersion = owner.Version.ToString() });
            }
            File.WriteAllText(forwarderOutput, JsonSerializer.Serialize(new {
                source = Path.GetFullPath(forwarderInput),
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(forwarderInput))), forwarders }, Json));
            return;
        }
        if (args.Length == 3 && args[0] == "--assembly-key")
        {
            using var input = File.OpenRead(args[1]);
            using var keyImage = new PEReader(input);
            var keyMetadata = keyImage.GetMetadataReader();
            var keyAssembly = keyMetadata.GetAssemblyDefinition();
            var publicKey = keyMetadata.GetBlobBytes(keyAssembly.PublicKey);
            if (publicKey.Length == 0) throw new InvalidDataException("The selected original assembly is not strong named.");
            File.WriteAllBytes(args[2], publicKey);
            Console.WriteLine(keyMetadata.GetString(keyAssembly.Name) + ", Version=" + keyAssembly.Version
                + ", PublicKeyToken=" + Convert.ToHexString(SHA1.HashData(publicKey)[^8..].Reverse().ToArray()).ToLowerInvariant());
            return;
        }
        if (args.Length == 3 && args[0] == "--replacement-version")
        {
            WriteReplacementVersion(args[1], args[2]);
            return;
        }
        if (args.Length == 3 && args[0] == "--inspect-suppressions")
        {
            MetadataInventory.WriteSuppressions(args[1], args[2]);
            return;
        }
        if (args.Length == 3 && args[0] == "--anonymous-property-roots")
        {
            MetadataInventory.WriteAnonymousPropertyRoots(args[1], args[2]);
            return;
        }

        if (args.Length is < 2 or > 3)
        {
            throw new ArgumentException("Usage: SourceMetadata <input.pdb> <fresh-output> [input.dll]");
        }

        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output))
        {
            throw new IOException("Refusing to replace existing metadata evidence: " + output);
        }

        Directory.CreateDirectory(output);
        using var stream = File.OpenRead(args[0]);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var documents = new List<object>();
        foreach (var handle in reader.Documents)
        {
            var document = reader.GetDocument(handle);
            var name = reader.GetString(document.Name);
            var algorithm = reader.GetGuid(document.HashAlgorithm);
            var hash = reader.GetBlobBytes(document.Hash);
            string? embeddedFile = null;
            foreach (var debugHandle in reader.GetCustomDebugInformation(handle))
            {
                var debug = reader.GetCustomDebugInformation(debugHandle);
                if (reader.GetGuid(debug.Kind) != EmbeddedSource)
                {
                    continue;
                }

                var bytes = reader.GetBlobBytes(debug.Value);
                var size = BitConverter.ToInt32(bytes);
                byte[] content;
                if (size == 0)
                {
                    content = bytes[4..];
                }
                else
                {
                    using var input = new MemoryStream(bytes, 4, bytes.Length - 4, false);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var decoded = new MemoryStream();
                    deflate.CopyTo(decoded);
                    content = decoded.ToArray();
                    if (content.Length != size)
                    {
                        throw new InvalidDataException("Embedded source size mismatch: " + name);
                    }
                }

                var actual = algorithm == Sha256 ? SHA256.HashData(content)
                    : algorithm == Sha1 ? SHA1.HashData(content)
                    : throw new InvalidDataException("Unsupported PDB source hash: " + algorithm);
                if (!actual.SequenceEqual(hash))
                {
                    throw new InvalidDataException("PDB source hash mismatch: " + name);
                }

                embeddedFile = Path.Combine(output, "embedded", Convert.ToHexString(SHA256.HashData(content)) + "-" + Path.GetFileName(name));
                Directory.CreateDirectory(Path.GetDirectoryName(embeddedFile)!);
                File.WriteAllBytes(embeddedFile, content);
            }

            documents.Add(new { document = name, hashAlgorithm = algorithm.ToString(), hash = Convert.ToHexString(hash), embeddedFile });
        }

        File.WriteAllText(Path.Combine(output, "documents.json"), JsonSerializer.Serialize(documents, Json));
        foreach (var handle in reader.CustomDebugInformation)
        {
            var debug = reader.GetCustomDebugInformation(handle);
            if (debug.Parent.Kind != HandleKind.ModuleDefinition)
            {
                continue;
            }

            var stem = Path.Combine(output, "module-" + reader.GetGuid(debug.Kind));
            var bytes = reader.GetBlobBytes(debug.Value);
            File.WriteAllBytes(stem + ".bin", bytes);
            File.WriteAllText(stem + ".txt", Encoding.UTF8.GetString(bytes).Replace('\0', '\n'));
        }

        if (args.Length == 2)
        {
            Console.WriteLine($"Verified {documents.Count} source documents; assembly inspection was not requested.");
            return;
        }

        using var assemblyStream = File.OpenRead(args[2]);
        using var image = new PEReader(assemblyStream);
        var metadata = image.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        var key = metadata.GetBlobBytes(definition.PublicKey);
        File.WriteAllBytes(Path.Combine(output, "public-key.snk"), key);
        File.WriteAllText(Path.Combine(output, "identity.json"), JsonSerializer.Serialize(new
        {
            name = metadata.GetString(definition.Name), version = definition.Version.ToString(),
            publicKeyToken = Convert.ToHexString(SHA1.HashData(key)[^8..].Reverse().ToArray()).ToLowerInvariant(),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))),
            pdbSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])))
        }, Json));

        var resourceDirectory = Path.Combine(output, "resources");
        Directory.CreateDirectory(resourceDirectory);
        var resources = new List<object>();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil)
            {
                throw new InvalidDataException("External assembly resources are unsupported.");
            }

            var name = metadata.GetString(resource.Name);
            if (Path.GetFileName(name) != name)
            {
                throw new InvalidDataException("Unsafe resource name: " + name);
            }

            var block = image.GetSectionData(image.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
            var blob = block.GetReader(checked((int)resource.Offset), block.Length - checked((int)resource.Offset));
            var content = blob.ReadBytes(blob.ReadInt32());
            File.WriteAllBytes(Path.Combine(resourceDirectory, name), content);
            resources.Add(new { name, sha256 = Convert.ToHexString(SHA256.HashData(content)), bytes = content.Length });
        }

        File.WriteAllText(Path.Combine(resourceDirectory, "integrity.json"), JsonSerializer.Serialize(resources, Json));
        Console.WriteLine($"Verified {documents.Count} source documents and {resources.Count} assembly resources.");
    }

    private static void WriteReplacementVersion(string input, string output)
    {
        using var stream = File.OpenRead(input);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        var attributes = new Dictionary<string, string>();
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection") continue;
            var name = metadata.GetString(type.Name);
            if (name is not ("AssemblyFileVersionAttribute" or "AssemblyInformationalVersionAttribute")) continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new BadImageFormatException("Invalid version attribute");
            attributes.Add(name, blob.ReadSerializedString() ?? throw new BadImageFormatException("Missing version"));
        }

        var version = Version.Parse(attributes["AssemblyFileVersionAttribute"]);
        if (assembly.Version.Major != 10 || version.Major != 10 || version.Revision is < 0 or >= ushort.MaxValue)
            throw new InvalidOperationException("Unsupported replacement framework version");
        var replacement = new Version(version.Major, version.Minor, version.Build, version.Revision + 1);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            source = Path.GetFullPath(input), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))),
            assemblyName = metadata.GetString(assembly.Name), assemblyVersion = assembly.Version.ToString(),
            fileVersion = version.ToString(), replacementFileVersion = replacement.ToString(),
            informationalVersion = attributes["AssemblyInformationalVersionAttribute"]
        }, Json));
    }
}
