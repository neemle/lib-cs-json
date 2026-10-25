# Ported finite JSON regression sources

These are real source fixtures imported from the MIT Neemle JSON runtime work.
`provenance/regression-source-import.json` records the original relative paths
and exact source hashes. They are retained here so the independent fork does not
lose its constructor, raw attribute, unsafe-path and inference regressions.

They are **source-only imports** at this stage. Earlier execution in the origin
checkout is not evidence that all have been built or run here. The independent
consumer actually executed here is `tests/SourceGenerated`, with its own proofs.

- `MemberConformance/Program.cs`: custom reflection, eager attribute effects,
  inheritance, fields/properties and finite metadata boundaries.
- `ConstructorRoles`: selected versus unused constructors, structs, classes,
  key/value pairs and statically proven F# mapping flags.
- `RawRoles`: converter/factory precedence, actual object members versus unused
  collection/simple-type raw attributes, and required-member short circuiting.
- `Library` and `UnsafeControl`: unused unsafe attributes remain inert; actual
  requested unsafe operations must retain genuine diagnostics.
- `MemberConformance/FSharp`: real F# record fixture; no native F# claim here.
- `Conformance`: enum names, polymorphism and canonical JSON owner inference.

The original README is preserved under `MemberConformance`; its paths and proof
names describe origin work, not executable dependencies of this source fork.
None of these files is included in the ordinary consumer implicitly.
