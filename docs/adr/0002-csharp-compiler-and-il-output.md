# ADR 0002: C# compiler implementation and IL output

Status: Accepted

## Decision

The compiler, runtime tooling, and CLI are implemented in C# on .NET 10. The
compiler emits ECMA-335 metadata and method bodies with
`System.Reflection.Metadata`. IL is generated output, not handwritten source.

The production path does not transpile RustSharp to C#, use `Reflection.Emit`,
or generate code at runtime. Generated assemblies must run on CoreCLR and pass
the supported .NET 10 Native AOT pipeline.

## Consequences

The code generator owns verifiable stack behavior, metadata identities,
portable debug information, deterministic output, and AOT reachability. Native
AOT and trimming warnings are treated as build failures rather than suppressed.
