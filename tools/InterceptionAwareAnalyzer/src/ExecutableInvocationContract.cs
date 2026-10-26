// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neemle.SystemTextJson.AotAnalysis;

internal static class ExecutableInvocationContract
{
    // A build-time compiler API call, never invocation of application methods.
    // Keep the prototype bound to the selected SDK's exact Roslyn API signature.
    private static readonly MethodInfo GetInterceptor = typeof(Microsoft.CodeAnalysis.CSharp.CSharpExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == "GetInterceptorMethod"
            && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] {
                typeof(SemanticModel), typeof(InvocationExpressionSyntax), typeof(CancellationToken) }));

    internal static IMethodSymbol Resolve(Compilation compilation, IInvocationOperation invocation)
    {
        IMethodSymbol original = invocation.TargetMethod;
        if (!original.IsStatic || original.IsExtensionMethod || invocation.Instance != null
            || invocation.Syntax is not InvocationExpressionSyntax syntax)
            return original;
        SemanticModel model = compilation.GetSemanticModel(syntax.SyntaxTree);
        var actual = (IMethodSymbol?)GetInterceptor.Invoke(null, new object[] { model, syntax, CancellationToken.None });
        // Roslyn returns the declared interceptor definition. The emitted call
        // applies the intercepted method's actual type arguments positionally.
        if (actual != null && actual.Arity != 0)
        {
            if (actual.Arity != original.Arity || original.TypeArguments.Length != actual.Arity)
                return original;
            actual = actual.ConstructedFrom.Construct(original.TypeArguments.ToArray());
        }
        if (actual == null || !actual.IsStatic || actual.IsExtensionMethod
            || actual.Parameters.Length != original.Parameters.Length || actual.RefKind != original.RefKind
            || !SymbolEqualityComparer.Default.Equals(actual.ReturnType, original.ReturnType))
            return original;
        for (int index = 0; index < actual.Parameters.Length; index++)
            if (actual.Parameters[index].RefKind != original.Parameters[index].RefKind
                || !SymbolEqualityComparer.Default.Equals(actual.Parameters[index].Type, original.Parameters[index].Type))
                return original;
        // The complete selected symbol reaches ProcessMethodCall. Its parameter,
        // return, generic and Requires contracts are handled by ordinary dataflow;
        // no emitted diagnostic is removed or rewritten.
        return actual;
    }
}
