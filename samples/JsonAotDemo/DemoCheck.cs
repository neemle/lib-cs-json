internal static class DemoCheck
{
    private static int failures;

    internal static void Expect(string name, bool passed, string result)
    {
        Console.WriteLine($"{name}: {(passed ? "PASS" : "FAIL")} {result}");
        if (!passed) failures++;
    }

    internal static void Run(string name, Action action)
    {
        try { action(); }
        catch (Exception error) { Unexpected(name, error); }
    }

    internal static async Task RunAsync(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { Unexpected(name, error); }
    }

    internal static void Complete()
    {
        if (failures == 0) Console.WriteLine("JSON_DEMO_PASS");
        else
        {
            Console.WriteLine($"JSON_DEMO_FAIL {failures}");
            Environment.ExitCode = 1;
        }
    }

    private static void Unexpected(string name, Exception error)
    {
        string type = error switch
        {
            System.Text.Json.JsonException => "JsonException",
            NotSupportedException => "NotSupportedException",
            InvalidOperationException => "InvalidOperationException",
            ArgumentException => "ArgumentException",
            IOException => "IOException",
            OperationCanceledException => "OperationCanceledException",
            _ => "Exception"
        };
        failures++;
        Console.WriteLine($"{name}: FAIL {type}");
    }
}
