// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Metadata;

namespace Neemle.DBAL.Tools;

/// <summary>Infers expression-tree method and field owners from actual IL tokens.</summary>
internal static class NativeMetadataTokenOwners
{
    internal static Type? Resolve(MetadataReader reader, NativeParameterMetadata.SignatureTypes signature,
        EntityHandle token, NativeParameterMetadata.GenericContext caller)
    {
        var member = token.Kind is HandleKind.MethodDefinition or HandleKind.MethodSpecification
            or HandleKind.MemberReference or HandleKind.FieldDefinition;
        while (token.Kind == HandleKind.MethodSpecification)
            token = reader.GetMethodSpecification((MethodSpecificationHandle)token).Method;
        token = token.Kind switch
        {
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)token).GetDeclaringType(),
            HandleKind.FieldDefinition => reader.GetFieldDefinition((FieldDefinitionHandle)token).GetDeclaringType(),
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)token).Parent,
            _ => token
        };
        var type = token.Kind switch
        {
            HandleKind.TypeDefinition => signature.GetTypeFromDefinition(reader, (TypeDefinitionHandle)token, 0),
            HandleKind.TypeReference => signature.GetTypeFromReference(reader, (TypeReferenceHandle)token, 0),
            HandleKind.TypeSpecification => signature.GetTypeFromSpecification(reader, caller, (TypeSpecificationHandle)token, 0),
            // Varargs MemberRefs may name a MethodDef parent; its owner still
            // identifies the parameter declarations, independently of varargs.
            HandleKind.MethodDefinition => signature.GetTypeFromDefinition(reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)token).GetDeclaringType(), 0),
            _ => null
        };
        // Attribute declarations belong to the generic definition. A token in an
        // open generic expression still proves that owner, but never proves an
        // invented closed signature or authorizes an unknown assembly receiver.
        return member && type?.IsConstructedGenericType == true && type.ContainsGenericParameters
            ? type.GetGenericTypeDefinition() : type;
    }
}
