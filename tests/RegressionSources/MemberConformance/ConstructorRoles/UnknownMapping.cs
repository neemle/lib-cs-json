// Copyright (c) Neemle contributors. Licensed under the MIT License.
namespace Microsoft.FSharp.Core {
    public enum SourceConstructFlags { None = 0, RecordType = 2, KindMask = 31 }
    [global::System.AttributeUsage(global::System.AttributeTargets.Struct)]
    public sealed class CompilationMappingAttribute : global::System.Attribute {
        public SourceConstructFlags SourceConstructFlags => (SourceConstructFlags)global::System.Environment.TickCount;
    }
}
namespace Neemle.DBAL.JsonMemberConformance {
    [global::Microsoft.FSharp.Core.CompilationMapping]
    public struct UnknownMappingStruct { public UnknownMappingStruct(int value) { Value = value; } public int Value { get; set; } }
}
