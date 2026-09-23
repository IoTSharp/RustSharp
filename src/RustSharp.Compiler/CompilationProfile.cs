namespace RustSharp.Compiler;

public enum CompilationProfile
{
    VerticalSlice,
    SafeCorePrimitives,
    SafeCoreTypes,
    SafeCoreGenerics,
    /// <summary>Bounded typed-MIR and ownership evidence profile.</summary>
    SafeCoreMir,
}
