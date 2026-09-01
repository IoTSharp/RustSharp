# ADR 0003: First vertical slice

Status: Accepted

## Decision

The first executable slice recognizes `fn main()` and string-literal
`println!` statements. It emits a managed executable assembly and runtime
configuration. The `rsc` command provides check, compile, run, and publish
operations.

Native AOT publishing may use a generated C# host adapter solely to provide the
SDK project boundary. Program logic remains in the RustSharp-generated IL
assembly. Replacing the adapter with direct SDK integration is a later build
system milestone.
