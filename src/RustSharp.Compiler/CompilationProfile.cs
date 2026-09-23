namespace RustSharp.Compiler;

public enum CompilationProfile
{
    VerticalSlice,
    SafeCorePrimitives,
    SafeCoreTypes,
    SafeCoreGenerics,
    /// <summary>Bounded typed-MIR and ownership evidence profile.</summary>
    SafeCoreMir,
    /// <summary>Versioned typed-MIR profile with structural-Copy repeated arrays enabled.</summary>
    SafeCoreMirV2,
}
