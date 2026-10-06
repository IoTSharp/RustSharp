using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace RustSharp.CodeGen.IL;

/// <summary>A fingerprint of one actual emitted CLR method body, including its execution header and handlers.</summary>
public sealed record RustSharpMetadataMethodBody(int Token, string Sha256);

public sealed partial record RustSharpMetadataDocument
{
    public const int MaximumMethodBodies = 16_384;

    /// <summary>Returns a document bound to the exact independently emitted CLR bodies.</summary>
    public RustSharpMetadataDocument WithMethodBodies(IEnumerable<RustSharpMetadataMethodBody> methodBodies)
    {
        ArgumentNullException.ThrowIfNull(methodBodies);
        ImmutableArray<RustSharpMetadataMethodBody> actual = NormalizeMethodBodies(methodBodies);
        if (!EmittedMethodBodies.IsEmpty && !EmittedMethodBodies.SequenceEqual(actual))
            throw new ArgumentException("Rust# metadata method body evidence does not match the actual emitted bodies.", nameof(methodBodies));
        return EmittedMethodBodies.IsEmpty
            ? new(Profile, SourceSha256, Functions, GenericInstances, TraitImplementations, MirSnapshot,
                Ownership, CleanupSnapshot, CallContracts, ValueTypes, SourceValueTypes, actual, SourceStructuralTypes)
            : this;
    }

    private static ImmutableArray<RustSharpMetadataMethodBody> NormalizeMethodBodies(
        IEnumerable<RustSharpMetadataMethodBody>? values)
    {
        if (values is null) return [];
        RustSharpMetadataMethodBody[] result = Materialize(values, MaximumMethodBodies, "emitted method bodies");
        var clock = Stopwatch.StartNew();
        var tokens = new HashSet<int>();
        foreach (RustSharpMetadataMethodBody value in result)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new ArgumentException("Rust# method body evidence exceeded its normalization time budget.", nameof(values));
            if (value is null || (value.Token & unchecked((int)0xff000000)) != 0x06000000 ||
                (value.Token & 0x00ffffff) == 0 || !tokens.Add(value.Token) || value.Sha256 is null ||
                value.Sha256.Length != 64 || value.Sha256.Any(static character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("Rust# emitted method body identity or fingerprint is invalid.", nameof(values));
        }
        return [.. result.OrderBy(static value => value.Token).Select(static value => value with { Sha256 = value.Sha256.ToUpperInvariant() })];
    }
}

/// <summary>Computes fingerprints from PE bodies without loading or executing producer code.</summary>
public static partial class RustSharpMetadataMethodBodies
{
    private const int MaximumBodyBytes = 64 * 1024 * 1024;

    public static ImmutableArray<RustSharpMetadataMethodBody> Capture(byte[] peImage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peImage);
        if (peImage.Length == 0 || peImage.Length > RustSharpMetadataConsumer.MaximumAssemblyBytes)
            throw new ArgumentException("Rust# emitted PE exceeds its body evidence size budget.", nameof(peImage));
        using var stream = new MemoryStream(peImage, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new BadImageFormatException("Method body evidence requires CLR metadata.");
        return Capture(pe, pe.GetMetadataReader(), cancellationToken);
    }

    internal static ImmutableArray<RustSharpMetadataMethodBody> Capture(PEReader pe, MetadataReader metadata,
        CancellationToken cancellationToken = default)
    {
        if (metadata.MethodDefinitions.Count > RustSharpMetadataDocument.MaximumMethodBodies)
            throw new InvalidDataException("Rust# actual method body count exceeds its evidence budget.");
        var clock = Stopwatch.StartNew();
        var result = ImmutableArray.CreateBuilder<RustSharpMetadataMethodBody>();
        long totalBytes = 0;
        byte[] referenceDigest = CaptureReferenceMetadata(pe, metadata, clock, ref totalBytes, cancellationToken);
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Rust# method body fingerprinting exceeded its time budget.");
            MethodDefinition definition = metadata.GetMethodDefinition(handle);
            if (definition.RelativeVirtualAddress == 0) continue;
            MethodBodyBlock body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            ImmutableArray<byte> il = body.GetILContent();
            if (il.Length > MaximumBodyBytes || (totalBytes += il.Length) > RustSharpMetadataConsumer.MaximumAssemblyBytes ||
                body.ExceptionRegions.Length > RustSharpMetadataDocument.MaximumMethodBodies)
                throw new InvalidDataException("Rust# actual method body exceeds its bounded evidence size.");
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                // IL carries table tokens, so unchanged IL can invoke a different
                // function if the referenced semantic metadata has drifted.
                writer.Write(referenceDigest.Length);
                writer.Write(referenceDigest);
                writer.Write(body.MaxStack);
                writer.Write(body.LocalVariablesInitialized);
                writer.Write(MetadataTokens.GetToken(body.LocalSignature));
                byte[] localSignature = body.LocalSignature.IsNil ? [] :
                    metadata.GetBlobBytes(metadata.GetStandaloneSignature(body.LocalSignature).Signature);
                if (localSignature.Length > MaximumBodyBytes || (totalBytes += localSignature.Length) > RustSharpMetadataConsumer.MaximumAssemblyBytes)
                    throw new InvalidDataException("Rust# actual local signatures exceed their bounded evidence size.");
                writer.Write(localSignature.Length);
                writer.Write(localSignature);
                writer.Write(il.Length);
                writer.Write(il.AsSpan());
                writer.Write(body.ExceptionRegions.Length);
                foreach (ExceptionRegion region in body.ExceptionRegions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.Elapsed > TimeSpan.FromSeconds(5))
                        throw new InvalidDataException("Rust# method handler fingerprinting exceeded its time budget.");
                    writer.Write((int)region.Kind);
                    writer.Write(region.TryOffset);
                    writer.Write(region.TryLength);
                    writer.Write(region.HandlerOffset);
                    writer.Write(region.HandlerLength);
                    writer.Write(region.FilterOffset);
                    writer.Write(MetadataTokens.GetToken(region.CatchType));
                }
            }
            result.Add(new(MetadataTokens.GetToken(handle), Convert.ToHexString(SHA256.HashData(
                payload.GetBuffer().AsSpan(0, checked((int)payload.Length))))));
        }
        return result.ToImmutable();
    }

    internal static void Validate(PEReader pe, MetadataReader metadata, RustSharpMetadataDocument document,
        List<string> diagnostics, CancellationToken cancellationToken = default)
    {
        bool required = !document.SourceValueTypes.IsEmpty || !document.SourceStructuralTypes.IsEmpty || document.CallContracts.Any(static contract =>
            contract.Schema == RustSharpMetadataCallContract.SourceSchema);
        if (document.EmittedMethodBodies.IsEmpty)
        {
            if (required) diagnostics.Add(RustSharpMetadataConsumer.InvalidMetadata +
                ": source contracts require evidence bound to the actual emitted method bodies.");
            return;
        }
        if (!document.EmittedMethodBodies.SequenceEqual(Capture(pe, metadata, cancellationToken)))
            diagnostics.Add(RustSharpMetadataConsumer.InvalidMetadata +
                ": source contract method body evidence contradicts the actual producer PE.");
    }
}
