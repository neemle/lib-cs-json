// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Neemle.DBAL.JsonMemberConformance;
internal static partial class Program
{
    private static void RunConstructorRoleChecks()
    {
        Check("unused-struct-constructor-marker", () => HasJsonConstructor(null, typeof(UnannotatedStruct).GetConstructor([typeof(int)])!));
        Check("derived-selected-constructor-raw", () => HasName(null, typeof(AdvertisedDerived).GetConstructor(Type.EmptyTypes)!, "missing", false));
        Check("kvp-selected-constructor-raw", () => HasName(null, typeof(KeyValuePair<string, int>).GetConstructor([typeof(string), typeof(int)])!, "missing", false));
    }
    private static bool TryRunConstructorRoleMode(string[] args, out int exit)
    {
        exit = 0;
        Type owner;
        if (args is ["--unknown-json-constructor"]) owner = typeof(UnknownMappingStruct);
        else if (args is ["--hierarchy-json-constructor"]) owner = typeof(UnadvertisedBase);
        else return false;
        if (RuntimeFeature.IsDynamicCodeSupported) { exit = 2; return true; }
        var constructor = owner.GetConstructor(owner == typeof(UnknownMappingStruct) ? [typeof(int)] : Type.EmptyTypes)!;
        try { _ = HasName(null, constructor, "missing", false); exit = 3; }
        catch (NotSupportedException error)
        {
            if (owner == typeof(UnknownMappingStruct) && !error.Message.Contains("SourceConstructFlags", StringComparison.Ordinal)) throw;
            Console.WriteLine(owner.Name + ":" + error.Message);
        }
        return true;
    }
}
