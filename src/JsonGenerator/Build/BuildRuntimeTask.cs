using Microsoft.Build.Framework;

namespace Neemle.Json.Generator;

public sealed class BuildRuntimeTask : Microsoft.Build.Utilities.Task
{
    [Required] public string RuntimeVersion { get; set; } = "";
    [Required] public string OutputFile { get; set; } = "";
    [Required] public string WorkingDirectory { get; set; } = "";

    public override bool Execute()
    {
        try
        {
            RuntimeBuilder.BuildAsync(RuntimeVersion, OutputFile, WorkingDirectory).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, true); return false; }
    }
}
