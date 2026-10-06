using System.Diagnostics;
using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

/// <summary>A possible enum variant at one exact returned value position.</summary>
public sealed record RustSharpMetadataSourceReturnVariant(IEnumerable<string> ValuePath, string VariantName);

/// <summary>Independent proof binding a re-exported source nominal to its owning producer.</summary>
public sealed record RustSharpMetadataSourceOwner(
    string AssemblyName,
    string SourceName,
    string ClrName,
    Guid ModuleVersionId,
    string SourceSha256,
    string AssemblySha256);

/// <summary>A structural source shape using the original producer's CLR value layout.</summary>
public sealed record RustSharpMetadataSourceStructuralType(
    string Type,
    string ClrName,
    RustSharpMetadataSourceOwner Owner);

public sealed partial record RustSharpMetadataDocument
{
    private static RustSharpMetadataSourceStructuralType[] NormalizeSourceStructuralTypes(
        IEnumerable<RustSharpMetadataSourceStructuralType>? values)
    {
        if (values is null) return [];
        RustSharpMetadataSourceStructuralType[] result = Materialize(values, MaximumValueTypes, "source structural owner bindings");
        var clock = Stopwatch.StartNew();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < result.Length; index++)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new ArgumentException("Source structural owner bindings exceeded their normalization time budget.", nameof(values));
            RustSharpMetadataSourceStructuralType value = result[index];
            if (value is null || value.Owner is null || string.IsNullOrWhiteSpace(value.ClrName) || value.ClrName.Length > 4096)
                throw new ArgumentException("Source structural owner binding is incomplete.", nameof(values));
            SafeCoreType type = SafeCoreSourceTypeCodec.Parse(value.Type);
            SafeCoreType original = SafeCoreSourceTypeCodec.Parse(value.Owner.SourceName);
            if (type.Kind is not (SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array) ||
                original.Kind != type.Kind || !identities.Add(value.Type) ||
                value.ClrName != value.Owner.AssemblyName + "::" + value.Owner.ClrName)
                throw new ArgumentException("Source structural owner shape or CLR identity is invalid.", nameof(values));
            ValidateSourceOwner(value.Owner);
            result[index] = value with { Type = SafeCoreSourceTypeCodec.Format(type) };
        }
        return result.OrderBy(static value => value.Type, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateSourceOwner(RustSharpMetadataSourceOwner owner)
    {
        if (string.IsNullOrWhiteSpace(owner.AssemblyName) || owner.AssemblyName.Length > 256 || owner.AssemblyName.Contains('\0') ||
            owner.AssemblyName.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')) ||
            owner.AssemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || owner.AssemblyName is "." or ".." ||
            string.IsNullOrWhiteSpace(owner.SourceName) || owner.SourceName.Length > 4096 || owner.SourceName.Contains('\0') ||
            string.IsNullOrWhiteSpace(owner.ClrName) || owner.ClrName.Length > 4096 || owner.ClrName.Contains('\0') ||
            owner.ModuleVersionId == Guid.Empty || !IsSha256(owner.SourceSha256) || !IsSha256(owner.AssemblySha256))
            throw new ArgumentException("Rust# source nominal or structural owner proof is invalid.", nameof(owner));

        static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static RustSharpMetadataSourceReturnVariant[]? NormalizeReturnVariants(
        IEnumerable<RustSharpMetadataSourceReturnVariant>? values)
    {
        if (values is null) return null;
        RustSharpMetadataSourceReturnVariant[] result = Materialize(values, MaximumCallTermsPerFunction, "source returned variants");
        var clock = Stopwatch.StartNew();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < result.Length; index++)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new ArgumentException("Source returned variants exceeded their normalization time budget.", nameof(values));
            RustSharpMetadataSourceReturnVariant value = result[index];
            if (value is null || string.IsNullOrWhiteSpace(value.VariantName) || value.VariantName.Length > 4096 ||
                value.VariantName.Contains('\0') || value.ValuePath is null)
                throw new ArgumentException("Source returned variant identity is invalid.", nameof(values));
            string[] path = Materialize(value.ValuePath, SafeCoreSourceOriginCodec.MaximumDepth, "source returned variant value path");
            foreach (string projection in path)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new ArgumentException("Source returned variant paths exceeded their normalization time budget.", nameof(values));
                SafeCoreMirProjection decoded = SafeCoreSourceOriginCodec.ParseProjection(projection);
                if (decoded.Kind is not (SafeCoreMirProjectionKind.Field or SafeCoreMirProjectionKind.TupleIndex or SafeCoreMirProjectionKind.ArrayIndex))
                    throw new ArgumentException("Source returned variants require exact initialized aggregate value paths.", nameof(values));
            }
            if (!identities.Add(string.Join('|', path) + "|" + value.VariantName))
                throw new ArgumentException("Source returned variant evidence is duplicated.", nameof(values));
            result[index] = new(path, value.VariantName);
        }
        return result;
    }
}
