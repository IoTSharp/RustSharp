namespace RustSharp.Compiler;

public sealed record CompilationOutput(
    string AssemblyPath,
    string? PdbPath,
    string RuntimeConfigPath);
