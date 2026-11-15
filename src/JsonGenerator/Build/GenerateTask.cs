using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Framework;

namespace Neemle.Json.Generator;

public sealed class GenerateTask : Microsoft.Build.Utilities.Task
{
    [Required] public string SourcesFile { get; set; } = "";
    [Required] public string ReferencesFile { get; set; } = "";
    [Required] public string OutputDirectory { get; set; } = "";
    [Required] public string DefinesFile { get; set; } = "";
    [Required] public string LanguageFile { get; set; } = "";
    [Required] public string NullableFile { get; set; } = "";
    [Required] public string AssemblyName { get; set; } = "";
    [Required] public string RoslynDirectory { get; set; } = "";
    [Required] public ITaskItem[] ComposerReferences { get; set; } = [];

    public override bool Execute()
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(GenerateTask).Assembly)!;
        context.Resolving += Resolve;
        try
        {
            SourceGenerator.Generate(SourcesFile, ReferencesFile, OutputDirectory, DefinesFile, LanguageFile, NullableFile,
                AssemblyName);
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, true); return false; }
        finally { context.Resolving -= Resolve; }
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        string? composer = ComposerReferences.Select(item => item.GetMetadata("FullPath"))
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path) == name.Name);
        string path = composer ?? Path.Combine(RoslynDirectory, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
