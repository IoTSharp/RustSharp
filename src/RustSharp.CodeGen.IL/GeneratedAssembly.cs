namespace RustSharp.CodeGen.IL;

public sealed record GeneratedAssembly(
    byte[] PeImage,
    byte[]? PdbImage,
    string RuntimeConfigJson,
    string? RustSharpMetadataJson = null)
{
    public bool RequiresMirRuntime { get; init; }
}
