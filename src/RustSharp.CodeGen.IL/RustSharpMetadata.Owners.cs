using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

public static partial class RustSharpMetadataConsumer
{
    private static readonly StringComparer OwnerPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison OwnerPathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record OwnerBinding(RustSharpMetadataSourceOwner Owner, string ClrName,
        RustSharpMetadataSourceValueType? Nominal = null, RustSharpMetadataSourceStructuralType? Structural = null);

    private sealed class OwnerReadContext
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly CancellationToken cancellationToken;
        private readonly HashSet<string> active = new(OwnerPathComparer);
        private readonly HashSet<string> visited = new(OwnerPathComparer);
        private readonly Dictionary<string, (string Name, Guid Mvid, string Hash)> identities = new(OwnerPathComparer);
        public Dictionary<string, RustSharpMetadataImportResult> Results { get; } = new(OwnerPathComparer);
        public string[] DependencyPaths { get; }
        public CancellationToken CancellationToken => cancellationToken;

        public OwnerReadContext(IEnumerable<string>? paths, CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
            var result = new List<string>();
            foreach (string path in (paths ?? []).Take(65))
            {
                Check();
                if (result.Count >= 64) throw new ArgumentException("Source owner resolution accepts at most 64 dependency paths.", nameof(paths));
                result.Add(Path.GetFullPath(path));
            }
            DependencyPaths = result.Distinct(OwnerPathComparer).ToArray();
        }

        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(10))
                throw new InvalidDataException("Source owner resolution exceeded its ten second read budget.");
        }

        public void Enter(string path)
        {
            Check();
            if (active.Contains(path)) throw new InvalidDataException("Source nominal owner dependencies contain a cycle.");
            if (active.Count >= 16 || !visited.Contains(path) && visited.Count >= 64)
                throw new InvalidDataException("Source nominal owner dependencies exceed their depth or assembly count budget.");
            active.Add(path);
            visited.Add(path);
        }

        public void Leave(string path) => active.Remove(path);

        public (string Name, Guid Mvid, string Hash) Inspect(string path)
        {
            Check();
            if (identities.TryGetValue(path, out var saved)) return saved;
            if (!visited.Contains(path) && visited.Count >= 64)
                throw new InvalidDataException("Source nominal owner candidates exceed their assembly count budget.");
            visited.Add(path);
            FileInfo info = new(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumAssemblyBytes)
                throw new InvalidDataException("Source owner PE is missing or exceeds its bounded size.");
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) throw new BadImageFormatException("Source owner candidate has no CLR metadata.");
            MetadataReader metadata = pe.GetMetadataReader();
            string name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            Guid mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            stream.Position = 0;
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            Check();
            var identity = (name, mvid, hash);
            identities[path] = identity;
            return identity;
        }
    }

    private static void ResolveOwnerBindings(MetadataReader metadata, RustSharpMetadataDocument document,
        string currentPath, string? currentAssemblyName, OwnerReadContext context,
        Dictionary<string, string> resolved, List<string> diagnostics)
    {
        var assemblyProofs = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<OwnerBinding> bindings = document.SourceValueTypes.Where(static value => value.Owner is not null)
            .Select(static value => new OwnerBinding(value.Owner!, value.ClrName, value))
            .Concat(document.SourceStructuralTypes.Select(static value => new OwnerBinding(value.Owner, value.ClrName, Structural: value)));
        foreach (OwnerBinding binding in bindings)
        {
            context.Check();
            RustSharpMetadataSourceOwner owner = binding.Owner;
            string fingerprint = owner.ModuleVersionId.ToString("D") + "|" + owner.SourceSha256.ToUpperInvariant() + "|" + owner.AssemblySha256.ToUpperInvariant();
            if (assemblyProofs.TryGetValue(owner.AssemblyName, out string? previous) && previous != fingerprint)
                throw new InvalidDataException("Source nominal owners contain conflicting assembly proofs.");
            assemblyProofs[owner.AssemblyName] = fingerprint;
            if (owner.AssemblyName == currentAssemblyName)
                throw new InvalidDataException("A re-exported source nominal cannot claim this same emitting assembly as its external owner.");
            string? match = null;
            string neighbor = Path.Combine(Path.GetDirectoryName(currentPath)!, owner.AssemblyName + ".dll");
            string[] candidates = new[] { neighbor }.Concat(context.DependencyPaths).Distinct(OwnerPathComparer).Take(65).ToArray();
            foreach (string candidate in candidates)
            {
                context.Check();
                if (!File.Exists(candidate) || string.Equals(candidate, currentPath, OwnerPathComparison)) continue;
                try
                {
                    var identity = context.Inspect(candidate);
                    if (identity.Name != owner.AssemblyName || identity.Mvid != owner.ModuleVersionId ||
                        !string.Equals(identity.Hash, owner.AssemblySha256, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!context.Results.TryGetValue(candidate, out RustSharpMetadataImportResult? imported))
                    {
                        imported = ReadAssemblyCore(candidate, document.Profile, null, context);
                        context.Results[candidate] = imported;
                    }
                    if (!imported.IsSuccessful || imported.Document is null ||
                        !string.Equals(imported.Document.SourceSha256, owner.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The independently bound source owner does not pass its producer PE validation.");
                    if (binding.Nominal is { } source)
                    {
                        RustSharpMetadataSourceValueType? original = imported.Document.SourceValueTypes.FirstOrDefault(value =>
                            value.Name == owner.SourceName && value.ClrName == owner.ClrName && value.Owner is null);
                        if (original is null || !OwnerLayoutMatches(document, currentAssemblyName!, source, imported.Document, owner.AssemblyName, original, context))
                            throw new InvalidDataException("Source nominal Copy, Drop, variant or ordered field facts contradict their original owning producer.");
                    }
                    else if (binding.Structural is { } structural)
                        ValidateStructuralOwner(document, currentAssemblyName!, structural, imported.Document, context);
                    ValidateOwnerTypeReference(metadata, binding.ClrName, owner, context);
                    match = candidate;
                    foreach ((string name, string path) in imported.ResolvedOwnerPaths)
                    {
                        context.Check();
                        AddResolvedOwner(name, path);
                    }
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
                {
                    // A malformed unrelated explicit dependency cannot establish an owner binding.
                }
            }
            if (match is null) AddContractDiagnostic(diagnostics, "source nominal owner proof cannot be resolved to a matching validated producer assembly.");
            else AddResolvedOwner(owner.AssemblyName, match);
        }

        void AddResolvedOwner(string name, string path)
        {
            if (resolved.TryGetValue(name, out string? previous) && !string.Equals(previous, path, OwnerPathComparison))
                throw new InvalidDataException("Source nominal owners resolve one assembly identity to conflicting dependency paths.");
            resolved[name] = path;
        }
    }

    private static void ValidateOwnerTypeReference(MetadataReader metadata, string clrName,
        RustSharpMetadataSourceOwner owner, OwnerReadContext context)
    {
        if (clrName != owner.AssemblyName + "::" + owner.ClrName)
            throw new InvalidDataException("Source external nominal CLR identity contradicts its original producer proof.");
        int count = metadata.GetTableRowCount(TableIndex.TypeRef);
        if (count > 16384) throw new InvalidDataException("Source owner TypeRef correlation exceeds its row budget.");
        bool found = false;
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            context.Check();
            TypeReference reference = metadata.GetTypeReference(handle);
            if (metadata.GetString(reference.Namespace) != "RustSharp.Generated.Values" || metadata.GetString(reference.Name) != owner.ClrName ||
                reference.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            AssemblyReference assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
            if (metadata.GetString(assembly.Name) == owner.AssemblyName) found = true;
        }
        if (!found) throw new InvalidDataException("Re-exported source nominal has no actual PE TypeRef to its original producer assembly.");
    }

    private static void ValidateStructuralOwner(RustSharpMetadataDocument consumer, string consumerAssembly,
        RustSharpMetadataSourceStructuralType source, RustSharpMetadataDocument producer, OwnerReadContext context)
    {
        context.Check();
        RustSharpMetadataSourceOwner owner = source.Owner;
        SafeCoreType shape = SafeCoreSourceTypeCodec.Parse(source.Type, context.CancellationToken);
        SafeCoreType original = SafeCoreSourceTypeCodec.Parse(owner.SourceName, context.CancellationToken);
        if (OwnerTypeKey(consumer, consumerAssembly, shape, context, 0) !=
            OwnerTypeKey(producer, owner.AssemblyName, original, context, 0))
            throw new InvalidDataException("Re-exported structural source shape contradicts its original producer types.");
        string expectedName = "mir_value_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            original.Kind + "|" + original))).ToLowerInvariant()[..24];
        if (owner.ClrName != expectedName)
            throw new InvalidDataException("Structural source owner CLR name contradicts its original producer source shape.");
        RustSharpMetadataValueType layout = producer.ValueTypes.FirstOrDefault(value => value.Name == owner.ClrName) ??
            throw new InvalidDataException("Structural source owner has no independently checked actual CLR layout.");
        RustSharpMetadataField[] fields = layout.Fields?.ToArray() ?? [];
        long count = original.Kind == SafeCoreSemanticTypeKind.Array ? original.Length!.Value : original.Elements.Count;
        if (count > RustSharpMetadataDocument.MaximumFieldsPerValueType || fields.Length != count)
            throw new InvalidDataException("Structural source owner field count contradicts its original source shape.");
        for (int index = 0; index < fields.Length; index++)
        {
            context.Check();
            SafeCoreType fieldType = original.Kind == SafeCoreSemanticTypeKind.Array ? original.ElementType : original.Elements[index];
            if (fields[index].Type != SourceClrType(producer, fieldType, isReturn: false))
                throw new InvalidDataException("Structural source owner fields contradict their independent producer CLR types.");
        }
    }

    private static bool OwnerLayoutMatches(RustSharpMetadataDocument consumer, string consumerAssembly,
        RustSharpMetadataSourceValueType source, RustSharpMetadataDocument producer, string producerAssembly,
        RustSharpMetadataSourceValueType original, OwnerReadContext context)
    {
        if (source.IsCopy != original.IsCopy || source.ConstructorKind != original.ConstructorKind ||
            source.IsPublic && !original.IsPublic) return false;
        string? sourceDrop = source.DropFunctionId is null ? null : FindFunction(producer, source.DropFunctionId)?.Name;
        string? originalDrop = original.DropFunctionId is null ? null : FindFunction(producer, original.DropFunctionId)?.Name;
        if (source.DropFunctionId is not null && sourceDrop is null || sourceDrop != originalDrop) return false;
        RustSharpMetadataSourceField[] left = source.Fields.ToArray();
        RustSharpMetadataSourceField[] right = original.Fields.ToArray();
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            context.Check();
            if (!FieldsEqual(left[index], right[index])) return false;
        }
        RustSharpMetadataSourceVariant[] variants = source.Variants?.ToArray() ?? [];
        RustSharpMetadataSourceVariant[] originals = original.Variants?.ToArray() ?? [];
        if (variants.Length != originals.Length) return false;
        for (int index = 0; index < variants.Length; index++)
        {
            context.Check();
            RustSharpMetadataSourceVariant variant = variants[index];
            RustSharpMetadataSourceVariant actual = originals[index];
            if (ShortName(variant.Name) != ShortName(actual.Name) || variant.Discriminant != actual.Discriminant ||
                variant.FieldOffset != actual.FieldOffset || variant.ConstructorKind != actual.ConstructorKind) return false;
            RustSharpMetadataSourceField[] payload = variant.Fields.ToArray();
            RustSharpMetadataSourceField[] actualPayload = actual.Fields.ToArray();
            if (payload.Length != actualPayload.Length) return false;
            for (int field = 0; field < payload.Length; field++)
            {
                context.Check();
                if (!FieldsEqual(payload[field], actualPayload[field])) return false;
            }
        }
        return true;

        bool FieldsEqual(RustSharpMetadataSourceField leftField, RustSharpMetadataSourceField rightField) =>
            leftField.Name == rightField.Name && leftField.IsPublic == rightField.IsPublic &&
            leftField.RequiresStaticLifetime == rightField.RequiresStaticLifetime &&
            OwnerTypeKey(consumer, consumerAssembly, SafeCoreSourceTypeCodec.Parse(leftField.Type), context, 0) ==
            OwnerTypeKey(producer, producerAssembly, SafeCoreSourceTypeCodec.Parse(rightField.Type), context, 0);

        static string ShortName(string name)
        {
            int separator = name.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? name : name[(separator + 2)..];
        }
    }

    private static string OwnerTypeKey(RustSharpMetadataDocument document, string assembly, SafeCoreType type,
        OwnerReadContext context, int depth)
    {
        context.Check();
        if (depth >= SafeCoreSourceTypeCodec.MaximumDepth) throw new InvalidDataException("Source owner field types exceed their structural depth budget.");
        if (type.Kind == SafeCoreSemanticTypeKind.Adt)
        {
            RustSharpMetadataSourceValueType layout = document.SourceValueTypes.FirstOrDefault(value => value.Name == type.Name) ??
                throw new InvalidDataException("Source owner field has no declared nominal layout.");
            return (layout.Owner?.AssemblyName ?? assembly) + "::" + (layout.Owner?.SourceName ?? layout.Name);
        }
        return type.Kind + "|" + type.IsMutable + "|" + type.Length + "|(" +
            string.Join(',', type.Elements.Select(value => OwnerTypeKey(document, assembly, value, context, depth + 1))) + ")";
    }
}
