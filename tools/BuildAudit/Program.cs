// Copyright (c) Neemle contributors. Licensed under the MIT License.
using Microsoft.Build.Logging;
using Microsoft.Build.Framework;
using System.Text.Json;
var commands = new List<object>();
var replay = new BinaryLogReplayEventSource();
replay.AnyEventRaised += (_, item) =>
{
    if (item is TaskCommandLineEventArgs task && task.TaskName is "Csc" or "Exec")
        commands.Add(new { projectId = task.BuildEventContext?.ProjectContextId, taskName = task.TaskName, command = task.CommandLine });
};
replay.Replay(args[0]);
File.WriteAllText(args[1], JsonSerializer.Serialize(new { commands }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Read {commands.Count} actual Csc/Exec command records.");
