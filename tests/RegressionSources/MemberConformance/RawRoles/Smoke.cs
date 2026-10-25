// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Text.Json;
using Neemle.DBAL.JsonRawRoles;
internal static partial class Program
{
    private static void RunRawRoleChecks()
    {
        Console.WriteLine("raw-role-enum:" + JsonSerializer.Serialize(EnumOwner.One, Context.Default.EnumOwner));
        Console.WriteLine("raw-role-collection:" + JsonSerializer.Serialize(new CollectionOwner(), Context.Default.CollectionOwner));
        Console.WriteLine("raw-role-converted:" + JsonSerializer.Serialize(new ConvertedOwner { Value = "x" }, Context.Default.ConvertedOwner));
        Console.WriteLine("raw-role-required:" + JsonSerializer.Serialize(new JsonRequiredOwner { Value = "x" }, Context.Default.JsonRequiredOwner));
        Console.WriteLine("raw-role-unused-effects:" + UnusedTypeAttribute.Count + ":" + UnusedPropertyAttribute.Count);
    }
}
