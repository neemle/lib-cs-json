[Flags]
public enum DemoPermissions
{
    None = 0,
    Read = 1,
    Write = 2,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("run")]
    Execute = 4,
    All = Read | Write | Execute
}
