namespace RustSharp.Compiler;

public enum CompilationProfile
{
    VerticalSlice,
    SafeCorePrimitives,
    SafeCoreTypes,
    SafeCoreGenerics,
    /// <summary>Experimental generic core profile with opt-in enum/Option/Result lowering.</summary>
    SafeCoreGenericsCore,
    /// <summary>Bounded typed-MIR and ownership evidence profile.</summary>
    SafeCoreMir,
    /// <summary>Versioned typed-MIR profile with structural-Copy repeated arrays enabled.</summary>
    SafeCoreMirV2,
}
