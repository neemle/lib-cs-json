// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Security.Cryptography;
using System.Text;

namespace Neemle.DBAL.Generators;

internal static class NativeMetadataNames
{
    internal const string RootType = "Neemle.DBAL.Inferred.ParameterMetadataRoots";
    internal const string JsonBindingsKey = "Neemle.DBAL.JsonTypeBindings.v1";
    internal const string PostgresJsonBindingsKey = "Neemle.DBAL.PostgreSQL.JsonTypeBindings.v1";
    internal const string PostgresJsonEnabledKey = "Neemle.DBAL.PostgreSQL.JsonEnabled.v1";
    internal const string DiagnosticPayloadBindingsKey = "Neemle.DBAL.DiagnosticPayloadBindings.v1";
    internal const string DiagnosticPayloadInputsKey = "Neemle.DBAL.DiagnosticPayloadInputs.v1";
    internal const string BindingsKey = "Neemle.DBAL.ParameterTypeBindings.v1";
    internal const string QueryIteratorsKey = "Neemle.DBAL.QueryIteratorBindings.v1";
    internal const string QueryShapesKey = "Neemle.DBAL.QueryShapeBindings.v1";

    internal static string QueryAssembly(string applicationName) => Assembly(applicationName) + ".Queryable";

    internal static string Assembly(string applicationName)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(applicationName));
        return "Neemle.DBAL.Inferred." + BitConverter.ToString(bytes, 0, 12).Replace("-", "").ToLowerInvariant();
    }
}
