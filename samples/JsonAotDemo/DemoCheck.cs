internal static class DemoCheck
{
    internal static void Expect(string name, bool passed, string result)
    {
        Console.WriteLine($"{name}: {(passed ? "PASS" : "FAIL")} {result}");
        if (!passed)
            throw new InvalidOperationException(name);
    }
}
