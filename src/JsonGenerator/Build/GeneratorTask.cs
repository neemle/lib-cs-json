using System.Diagnostics;
using Microsoft.Build.Framework;

namespace Neemle.Json.Generator;

public sealed class GeneratorTask : Microsoft.Build.Utilities.Task
{
    [Required] public string Tool { get; set; } = "";
    [Required] public string Mode { get; set; } = "";
    [Required] public string WorkingDirectory { get; set; } = "";
    [Required] public string Arg1 { get; set; } = "";
    [Required] public string Arg2 { get; set; } = "";
    [Required] public string Arg3 { get; set; } = "";
    public string? Arg4 { get; set; }
    public string? Arg5 { get; set; }
    public string? Arg6 { get; set; }

    public override bool Execute()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = WorkingDirectory
        };
        foreach (string? argument in new[] { Tool, Mode, Arg1, Arg2, Arg3, Arg4, Arg5, Arg6 })
            if (argument is not null) start.ArgumentList.Add(argument);
        using Process? process = Process.Start(start);
        if (process is null) { Log.LogError("Could not start JSON generator."); return false; }
        process.WaitForExit();
        if (process.ExitCode == 0) return true;
        Log.LogError("JSON generator failed: " + process.ExitCode);
        return false;
    }
}
