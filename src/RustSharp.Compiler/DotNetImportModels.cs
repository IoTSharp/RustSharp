using System.Collections.Immutable;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>An explicit immutable reference lock. Metadata is read; assembly code is never loaded.</summary>
public sealed record DotNetReferenceLock(string Path, string AssemblyName, string Version, string Sha256);

public sealed record DotNetImportDeclaration(string Alias, string AssemblyName, string TypeName,
    string MemberName, string? Signature, ImmutableArray<string> ParameterTypes, string ReturnType,
    TextSpan Span, TextSpan AliasSpan, TextSpan MemberSpan, TextSpan SignatureSpan);

public sealed record DotNetBoundImport(DotNetImportDeclaration Declaration, string MemberContractId,
    string DefinitionAssemblyName, string DefinitionAssemblyVersion, string ReferenceSha256,
    Guid ModuleVersionId, int MethodToken, string ClosedSignature, ImmutableArray<string> GenericArguments);

/// <summary>Successful results expose the complete binding inventory; failures expose no partial bindings.</summary>
public sealed record DotNetImportBindingResult(ImmutableArray<DotNetBoundImport> Imports,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => Diagnostics.IsEmpty;
    public string Profile { get; } = DotNetInteropContract.Profile;
    public string ContractSha256 { get; } = DotNetInteropContract.FrozenContractSha256;
    // P2-06.02 proves source/metadata binding. Runtime lowering is a separate leaf.
    public bool RuntimeEvidence { get; }
}
