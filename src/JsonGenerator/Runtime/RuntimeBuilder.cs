using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Neemle.Json.Generator;

internal static class RuntimeBuilder
{
    private static readonly string[] SourcePaths =
    [
        "eng/", "src/libraries/System.Text.Json/", "src/libraries/Common/",
        "src/libraries/System.Private.CoreLib/", "src/libraries/System.Runtime.InteropServices/gen/",
        "src/libraries/System.Text.RegularExpressions/gen/", "src/libraries/Microsoft.NETCore.Platforms/"
    ];

    internal static async Task BuildAsync(string version, string output, string parent, string sourceArchive)
    {
        if (!Version.TryParse(version, out var parsed) || parsed.Major is not (10 or 11) || parsed.Build < 0)
            throw new ArgumentException("Invalid runtime version.", nameof(version));
        string work = Path.Combine(parent, "runtime." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await ExtractAsync(version, work, sourceArchive);
            string tools = Path.GetDirectoryName(typeof(RuntimeBuilder).Assembly.Location)!;
            ApplyPatch(work, Path.Combine(tools, "JsonRuntime.patch"), parsed.Major == 11);
            if (parsed.Major == 11) ApplyPatch(work, Path.Combine(tools, "JsonRuntime.Net11.patch"), false);
            await BuildRuntimeAsync(work, output, parsed.Major, !string.IsNullOrEmpty(sourceArchive));
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task ExtractAsync(string version, string root, string sourceArchive)
    {
        using var client = new HttpClient();
        using var response = string.IsNullOrEmpty(sourceArchive) ?
            await client.GetAsync($"https://codeload.github.com/dotnet/runtime/tar.gz/refs/tags/v{version}",
                HttpCompletionOption.ResponseHeadersRead) : null;
        response?.EnsureSuccessStatusCode();
        await using var compressed = response is null ? File.OpenRead(sourceArchive) : await response.Content.ReadAsStreamAsync();
        await using var archive = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(archive);
        bool verified = false;
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            int separator = entry.Name.IndexOf('/');
            if (separator < 0) continue;
            if (!verified)
            {
                if (!entry.Name[..separator].Equals("runtime-" + version, StringComparison.Ordinal))
                    throw new InvalidDataException("The runtime source archive does not match " + version + ".");
                verified = true;
            }
            string relative = entry.Name[(separator + 1)..];
            if (relative.Length == 0 || !RequiredSource(relative)) continue;
            await ExtractEntryAsync(entry, root, relative);
        }
        if (!verified) throw new InvalidDataException("The runtime source archive is empty.");
    }

    private static bool RequiredSource(string path) =>
        SourcePaths.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)) ||
        !path.Contains('/') ||
        path.StartsWith("src/", StringComparison.Ordinal) && path.Count(c => c == '/') == 1 ||
        path.StartsWith("src/libraries/", StringComparison.Ordinal) && path.Count(c => c == '/') == 2;

    private static async Task ExtractEntryAsync(TarEntry entry, string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid archive entry path.");
        if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(path); return; }
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            throw new InvalidDataException($"Unsupported archive entry: {relative}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(file);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode);
    }

    private static void ApplyPatch(string root, string patchPath, bool net11)
    {
        string[] lines = File.ReadAllLines(patchPath);
        int index = 0;
        while (index < lines.Length)
        {
            if (!lines[index].StartsWith("diff --git ", StringComparison.Ordinal)) { index++; continue; }
            string header = lines[index++];
            int split = header.LastIndexOf(" b/", StringComparison.Ordinal);
            if (split < 0) throw new InvalidDataException("Invalid patch header.");
            string relative = header[(split + 3)..];
            int end = index;
            while (end < lines.Length && !lines[end].StartsWith("diff --git ", StringComparison.Ordinal)) end++;
            if (!net11 || !ExcludedOnNet11(relative)) ApplyFile(root, relative, lines, index, end);
            index = end;
        }
    }

    private static bool ExcludedOnNet11(string path) =>
        path.EndsWith("/JsonSerializer.Read.HandleMetadata.cs", StringComparison.Ordinal) ||
        path.EndsWith("/Serialization/JsonStringEnumConverter.cs", StringComparison.Ordinal) ||
        path.EndsWith("/Metadata/JsonPropertyInfo.cs", StringComparison.Ordinal) ||
        path.EndsWith("/Metadata/JsonTypeInfo.cs", StringComparison.Ordinal);

    private static void ApplyFile(string root, string relative, string[] patch, int start, int end)
    {
        string path = Path.Combine(root, relative);
        string[] original = File.Exists(path) ? File.ReadAllLines(path) : [];
        var result = new List<string>();
        int position = 0;
        for (int index = start; index < end; index++)
        {
            if (!patch[index].StartsWith("@@ ", StringComparison.Ordinal)) continue;
            var match = Regex.Match(patch[index], @"^@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@");
            if (!match.Success) throw new InvalidDataException($"Invalid patch hunk in {relative}.");
            int expected = Math.Max(0, int.Parse(match.Groups[1].Value) - 1);
            int target = FindHunk(original, position, expected, patch, index + 1, end);
            if (target < 0) throw new InvalidDataException($"Patch hunk does not match {relative} near line {expected + 1}.");
            while (position < target) result.Add(original[position++]);
            while (++index < end && !patch[index].StartsWith("@@ ", StringComparison.Ordinal))
                ApplyLine(relative, patch[index], original, result, ref position);
            index--;
        }
        result.AddRange(original.Skip(position));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', result) + "\n");
    }

    private static int FindHunk(string[] original, int minimum, int expected, string[] patch, int start, int end)
    {
        var context = new List<string>();
        for (int i = start; i < end && !patch[i].StartsWith("@@ ", StringComparison.Ordinal); i++)
            if (patch[i].Length > 0 && patch[i][0] is ' ' or '-') context.Add(patch[i][1..]);
        int best = -1;
        int distance = int.MaxValue;
        for (int i = minimum; i <= original.Length - context.Count; i++)
        {
            int candidateDistance = Math.Abs(i - expected);
            if (candidateDistance >= distance) continue;
            bool matches = true;
            for (int j = 0; j < context.Count && matches; j++) matches = original[i + j] == context[j];
            if (!matches) continue;
            best = i;
            distance = candidateDistance;
        }
        return best;
    }

    private static void ApplyLine(string path, string line, string[] original, List<string> result, ref int position)
    {
        if (line.Length == 0) throw new InvalidDataException($"Invalid patch line in {path}.");
        if (line[0] == '+') { result.Add(line[1..]); return; }
        if (line[0] is not (' ' or '-')) throw new InvalidDataException($"Invalid patch operation in {path}.");
        if (position >= original.Length || original[position] != line[1..])
            throw new InvalidDataException($"Patch does not match {path} at line {position + 1}.");
        if (line[0] == ' ') result.Add(original[position]);
        position++;
    }

    private static async Task BuildRuntimeAsync(string root, string output, int major, bool offline)
    {
        string linker = Path.Combine(root, "eng", "illink.targets");
        File.WriteAllText(linker, File.ReadAllText(linker).Replace("TaskFactory=\"TaskHostFactory\"", ""));
        string references = Path.Combine(root, "framework-refs.targets");
        File.WriteAllText(references, "<Project><ItemGroup><ProjectReference Remove=\"@(ProjectReference)\" /></ItemGroup></Project>");
        string framework = $"net{major}.0";
        string project = Path.Combine(root, "src", "libraries", "System.Text.Json", "src", "System.Text.Json.csproj");
        string[] args = ["build", project, "-f", framework, "-c", "Release", $"-p:TargetFrameworks={framework}",
            $"-p:CustomAfterMicrosoftCommonTargets={references}", "-p:BuildProjectReferences=false",
            "-p:UseLocalTargetingRuntimePack=false", "-p:DisableImplicitFrameworkReferences=false",
            "-p:_RequiresLiveILLink=false", "-p:DotNetBuildSourceOnly=true", "-p:TreatWarningsAsErrors=true",
            "-p:NoWarn=", "-v:quiet"];
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (offline) start.ArgumentList.Add("-p:NuGetAudit=false");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
            start.Environment["NUGET_PACKAGES"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Runtime build failed.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(Path.Combine(root, "artifacts", "bin", "System.Text.Json", "Release", framework, "System.Text.Json.dll"), output, true);
    }
}
