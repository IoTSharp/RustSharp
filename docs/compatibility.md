# Compatibility contract

The initial language baseline is Rust 1.98.0, Edition 2024. Compatibility is
declared by profile and measured separately for syntax, semantics, public API,
runtime behavior, binary format, and wire protocols.

The first profile is `vertical-slice-v1`. It supports only `fn main()` with
zero or more `println!(string-literal);` statements. It is not described as
full Rust compatibility.

RustSharp will not silently reinterpret unsupported Rust constructs as C# or
CLR constructs. Unsupported input must produce a stable diagnostic. Rust ABI,
`.rlib` binary compatibility, and `repr(Rust)` compatibility are not promised.
Explicit C ABI and .NET interop are separate, versioned contracts.
