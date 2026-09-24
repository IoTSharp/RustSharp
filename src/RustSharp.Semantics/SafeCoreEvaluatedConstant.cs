namespace RustSharp.Semantics;

/// <summary>
/// Immutable values exported by the bounded const interpreter. Scalar text uses
/// MIR invariant spelling; aggregate fields retain declaration names and
/// constructors retain their resolved identity. References contain their
/// immutable promoted referent as the sole element.
/// </summary>
public sealed record SafeCoreEvaluatedConstant(
    SafeCoreType Type,
    string? Scalar,
    IReadOnlyList<SafeCoreEvaluatedConstant> Elements,
    IReadOnlyDictionary<string, SafeCoreEvaluatedConstant> Fields,
    string? Constructor);
