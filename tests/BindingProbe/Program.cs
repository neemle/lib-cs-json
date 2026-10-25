// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Security.Cryptography;
using System.Text.Json;
var assembly = typeof(JsonSerializer).Assembly;
Console.WriteLine(JsonSerializer.Serialize(new { assembly = assembly.FullName, path = assembly.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() }));
