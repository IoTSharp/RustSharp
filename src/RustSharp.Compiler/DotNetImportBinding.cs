using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>Exact, metadata-only source imports against the immutable dotnet-interop-v1 member inventory.</summary>
public static class DotNetImportBinding
{
    public const int MaximumReferenceAssemblies = 32;
    public const int MaximumMetadataBytesPerAssembly = 16_777_216;
    public const int MaximumCandidateMembers = 256;
    public const int MaximumGenericArguments = 16;
    public const int MaximumBoundaryParameters = 256;

    public static DotNetImportBindingResult Bind(string source, string sourcePath,
        IReadOnlyList<DotNetReferenceLock> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(references);
        var budget = new DotNetBindingBudget(cancellationToken);
        var assemblies = new Dictionary<string, Reference>(StringComparer.Ordinal);
        TextSpan anchor = default;
        try
        {
            budget.Step();
            ImmutableArray<DotNetImportDeclaration> declarations = new DotNetImportParser(source, sourcePath, budget).Parse();
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (DotNetImportDeclaration declaration in declarations)
            {
                budget.Step();
                if (!aliases.Add(declaration.Alias) || !identities.Add(declaration.AssemblyName + "|" +
                    declaration.TypeName + "|" + declaration.MemberName + "|" + declaration.Signature))
                    throw new DotNetBindingFailure("RSDN1002", "Duplicate import alias or managed identity.", declaration.AliasSpan);
            }
            if (references.Count is 0 or > MaximumReferenceAssemblies)
                throw new DotNetBindingFailure("RSDN1008", "Provide 1 to 32 explicit locked metadata references.", declarations[0].Span);
            anchor = declarations[0].Span;
            foreach (DotNetReferenceLock reference in references)
            {
                budget.Step();
                if (reference is null) throw new DotNetBindingFailure("RSDN1008", "Null reference lock entry.", anchor);
                var loaded = new Reference(reference, budget, anchor);
                if (!assemblies.TryAdd(reference.AssemblyName, loaded))
                {
                    loaded.Dispose();
                    throw new DotNetBindingFailure("RSDN1008", "Duplicate assembly identity in reference lock.", anchor);
                }
            }
            var result = ImmutableArray.CreateBuilder<DotNetBoundImport>();
            foreach (DotNetImportDeclaration declaration in declarations)
            {
                budget.Step(); anchor = declaration.MemberSpan;
                result.Add(BindOne(declaration, assemblies, budget));
            }
            return new(result.ToImmutable(), []);
        }
        catch (DotNetBindingFailure failure)
        {
            return new([], [new(failure.Code, failure.Message, failure.Span) { SourcePath = sourcePath }]);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
        {
            return new([], [new("RSDN1008", "Locked reference could not be read as valid managed metadata: " + failure.GetType().Name,
                anchor) { SourcePath = sourcePath }]);
        }
        finally
        {
            foreach (Reference reference in assemblies.Values) reference.Dispose();
        }
    }

    private static DotNetBoundImport BindOne(DotNetImportDeclaration declaration,
        Dictionary<string, Reference> assemblies, DotNetBindingBudget budget)
    {
        string member = declaration.MemberName;
        var arguments = ImmutableArray<string>.Empty;
        int opening = member.IndexOf('<');
        if (opening >= 0)
        {
            if (!member.EndsWith('>') || opening == 0)
                Fail("RSDN1006", "Malformed closed generic member identity.");
            arguments = member[(opening + 1)..^1].Split(',').ToImmutableArray();
            member = member[..opening];
            if (arguments.Length is 0 or > MaximumGenericArguments || arguments.Any(argument =>
                argument is not ("System.Int32" or "System.Boolean" or "System.String")))
                Fail("RSDN1006", "Open or unsupported managed generic argument.");
        }
        if (!DotNetImportParser.MetadataName(member, false)) Fail("RSDN1001", "Invalid member metadata name.");
        if (!assemblies.TryGetValue(declaration.AssemblyName, out Reference? root))
            Fail("RSDN1008", "Requested assembly is absent from the explicit reference lock.");
        var (reference, typeHandle) = FindType(root!, declaration.TypeName, assemblies, budget, declaration.MemberSpan);
        TypeDefinition type = reference.Reader.GetTypeDefinition(typeHandle);
        if (!PublicType(reference.Reader, typeHandle, budget)) Fail("RSDN1003", "Declaring type is inaccessible.");
        var candidates = new List<(MethodDefinitionHandle Handle, MethodDefinition Method)>();
        int examined = 0;
        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            budget.Step();
            if (++examined > MaximumCandidateMembers) Fail("RSDN1005", "Candidate member limit of 256 exceeded.");
            MethodDefinition method = reference.Reader.GetMethodDefinition(handle);
            if (reference.Reader.GetString(method.Name) == member) candidates.Add((handle, method));
        }
        if (candidates.Count == 0) Fail("RSDN1005", "Member is absent or unlisted.");
        if (declaration.Signature is null && candidates.Count > 1) Fail("RSDN1004", "Overloaded member requires a complete exact signature.");
        if (declaration.Signature is null) Fail("RSDN1001", "Import requires a complete exact signature.");
        var matches = new List<(MethodDefinitionHandle Handle, MethodDefinition Method, string Signature)>();
        bool genericRejected = false;
        foreach (var candidate in candidates)
        {
            budget.Step();
            if (candidate.Method.GetGenericParameters().Count != arguments.Length)
            {
                genericRejected = true; continue;
            }
            var provider = new SignatureProvider(reference, arguments, assemblies, budget, declaration.MemberSpan);
            MethodSignature<string> signature;
            try
            {
                ValidateSignature(reference.Reader.GetBlobReader(candidate.Method.Signature), budget, declaration.MemberSpan);
                signature = candidate.Method.DecodeSignature(provider, genericContext: null);
            }
            catch (DotNetBindingFailure failure) when (failure.Code == "RSDN1005") { continue; }
            if (signature.ParameterTypes.Length > MaximumBoundaryParameters) Fail("RSDN1005", "Boundary parameter limit exceeded.");
            string full = signature.ReturnType + "(" + string.Join(',', signature.ParameterTypes) + ")";
            if (full != declaration.Signature) continue;
            if (signature.Header.CallingConvention != SignatureCallingConvention.Default ||
                (candidate.Method.Attributes & (MethodAttributes.PinvokeImpl | MethodAttributes.UnmanagedExport)) != 0)
                Fail("RSDN1010", "Unmanaged calling conventions, P/Invoke and unmanaged exports are excluded.");
            if ((candidate.Method.Attributes & MethodAttributes.Static) == 0)
                Fail("RSDN1010", "Implicit instance dispatch is excluded; use the declared static adapter.");
            if ((candidate.Method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                Fail("RSDN1003", "Requested member is inaccessible.");
            if (!Constraints(reference, candidate.Method, arguments, assemblies, budget, declaration.MemberSpan))
            {
                genericRejected = true; continue;
            }
            string sourceSignature = declaration.ReturnType + "(" + string.Join(',', declaration.ParameterTypes) + ")";
            if (sourceSignature != full)
                throw new DotNetBindingFailure("RSDN1005", "Source parameter/result mapping disagrees with the exact CLR signature.", declaration.SignatureSpan);
            matches.Add((candidate.Handle, candidate.Method, full));
        }
        if (matches.Count == 0) Fail(genericRejected ? "RSDN1006" : "RSDN1005",
            genericRejected ? "Open generic or unsatisfied metadata generic constraint." : "No matching exact signature.");
        if (matches.Count != 1) Fail("RSDN1004", "More than one exact metadata signature matches.");
        string contract = ContractMember(declaration);
        if (contract.Length == 0) Fail("RSDN1005", "Member/signature is outside the frozen v1 inventory.");
        return new(declaration, contract, reference.Name, reference.Version, reference.Sha256,
            reference.Reader.GetGuid(reference.Reader.GetModuleDefinition().Mvid),
            MetadataTokens.GetToken(matches[0].Handle), matches[0].Signature, arguments);

        void Fail(string code, string message) => throw new DotNetBindingFailure(code, message, declaration.MemberSpan);
    }

    private static string ContractMember(DotNetImportDeclaration item) =>
        (item.AssemblyName, item.TypeName, item.MemberName, item.Signature) switch
        {
            ("System.Runtime", "System.Math", "Abs", "System.Int32(System.Int32)") => "bcl-abs-i32",
            ("InteropFixtures", "InteropFixtures.Math", "Add", "System.Int32(System.Int32,System.Int32)") => "fixture-add-i32",
            ("InteropFixtures", "InteropFixtures.Algorithms", "Identity<System.Int32>", "System.Int32(System.Int32)") => "fixture-identity-i32",
            ("InteropFixtures", "InteropFixtures.CounterAdapters", "Read", "System.Int32(InteropFixtures.Counter)") => "fixture-counter-read",
            ("InteropFixtures", "InteropFixtures.CounterAdapters", "Release", "System.Void(InteropFixtures.Counter)") => "fixture-counter-release",
            // Locked NuGet dependency closure and AOT reachability are separately required by P2-06.05.
            ("RustSharp.Interop.Adapters", "RustSharp.Interop.Adapters.StringSegmentAdapters", "Length", "System.Int32(System.String)") => "nuget-stringsegment-length",
            _ => "",
        };

    private static void ValidateSignature(BlobReader blob, DotNetBindingBudget budget, TextSpan span)
    {
        if (blob.Length is 0 or > 4096) throw new DotNetBindingFailure("RSDN1005", "Method signature blob exceeds the v1 bound.", span);
        byte header = blob.ReadByte();
        if ((header & 15) == 5) throw new DotNetBindingFailure("RSDN1010", "Varargs are excluded.", span);
        if ((header & 16) != 0 && blob.ReadCompressedInteger() > MaximumGenericArguments)
            throw new DotNetBindingFailure("RSDN1006", "Generic arity exceeds 16.", span);
        int count = blob.ReadCompressedInteger();
        if (count is < 0 or > MaximumBoundaryParameters)
            throw new DotNetBindingFailure("RSDN1005", "Boundary parameter limit exceeded.", span);
        // The admitted method types are nonrecursive scalars/handles or a substituted method generic.
        for (int index = 0; index <= count; index++)
        {
            budget.Step(); byte element = blob.ReadByte();
            if (element is 0x11 or 0x12)
            {
                int token = blob.ReadCompressedInteger();
                if (token < 0 || (token & 3) == 2)
                    throw new DotNetBindingFailure("RSDN1005", "TypeSpec parameters require a newer signature profile.", span);
            }
            else if (element == 0x1e) { if (blob.ReadCompressedInteger() < 0) throw new BadImageFormatException(); }
            else if (element is 0x0f or 0x1b)
                throw new DotNetBindingFailure("RSDN1010", "Pointers and callbacks are excluded.", span);
            else if (element is not (0x01 or 0x02 or 0x08 or 0x0e or 0x1c))
                throw new DotNetBindingFailure("RSDN1005", "Unsupported metadata boundary type.", span);
        }
        if (blob.RemainingBytes != 0) throw new DotNetBindingFailure("RSDN1005", "Unexpected signature tail.", span);
    }

    private static bool PublicType(MetadataReader reader, TypeDefinitionHandle handle, DotNetBindingBudget budget)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            budget.Step();
            TypeDefinition type = reader.GetTypeDefinition(handle);
            TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
            TypeDefinitionHandle parent = type.GetDeclaringType();
            if (parent.IsNil) return visibility == TypeAttributes.Public;
            if (visibility != TypeAttributes.NestedPublic) return false;
            handle = parent;
        }
        return false;
    }

    private static (Reference Reference, TypeDefinitionHandle Type) FindType(Reference reference, string fullName,
        Dictionary<string, Reference> assemblies, DotNetBindingBudget budget, TextSpan span)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int hop = 0; hop < MaximumReferenceAssemblies; hop++)
        {
            budget.Step();
            if (!seen.Add(reference.Name)) break;
            foreach (TypeDefinitionHandle handle in reference.Reader.TypeDefinitions)
            {
                budget.Step();
                TypeDefinition type = reference.Reader.GetTypeDefinition(handle);
                if (reference.Reader.GetString(type.Namespace) + "." + reference.Reader.GetString(type.Name) == fullName)
                    return (reference, handle);
            }
            AssemblyReferenceHandle forwarded = default;
            foreach (ExportedTypeHandle handle in reference.Reader.ExportedTypes)
            {
                budget.Step();
                ExportedType exported = reference.Reader.GetExportedType(handle);
                if (reference.Reader.GetString(exported.Namespace) + "." + reference.Reader.GetString(exported.Name) == fullName &&
                    exported.IsForwarder && exported.Implementation.Kind == HandleKind.AssemblyReference)
                {
                    if (!forwarded.IsNil)
                        throw new DotNetBindingFailure("RSDN1008", "Ambiguous type forwarder identity.", span);
                    forwarded = (AssemblyReferenceHandle)exported.Implementation;
                }
            }
            if (forwarded.IsNil) throw new DotNetBindingFailure("RSDN1005", "Declaring type is absent or unlisted.", span);
            reference = LockedAssemblyScope(reference, forwarded, assemblies, budget, span);
        }
        throw new DotNetBindingFailure("RSDN1008", "Type forwarding cycle or excessive forwarding depth.", span);
    }

    private static bool Constraints(Reference reference, MethodDefinition method, ImmutableArray<string> arguments,
        Dictionary<string, Reference> assemblies, DotNetBindingBudget budget, TextSpan span)
    {
        var indices = new HashSet<int>();
        foreach (GenericParameterHandle handle in method.GetGenericParameters())
        {
            budget.Step(); GenericParameter parameter = reference.Reader.GetGenericParameter(handle);
            if (parameter.Index < 0 || parameter.Index >= arguments.Length || !indices.Add(parameter.Index))
                throw new DotNetBindingFailure("RSDN1006", "Generic parameter indices must be unique and inside the closed argument list.", span);
            string argument = arguments[parameter.Index];
            bool valueType = argument is "System.Int32" or "System.Boolean";
            if ((parameter.Attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && valueType) return false;
            if ((parameter.Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 && !valueType) return false;
            if ((parameter.Attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !valueType) return false;
            var provider = new SignatureProvider(reference, arguments, assemblies, budget, span, constraint: true);
            foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
            {
                budget.Step();
                EntityHandle constraint = reference.Reader.GetGenericParameterConstraint(constraintHandle).Type;
                string expected = provider.Entity(constraint);
                if (expected == "System.ValueType" && valueType) continue;
                if (expected == "System.Object") continue;
                if (!Assignable(argument, expected, assemblies, budget, span)) return false;
            }
        }
        return true;
    }

    private static bool Assignable(string argument, string expected, Dictionary<string, Reference> assemblies,
        DotNetBindingBudget budget, TextSpan span)
    {
        if (argument == expected) return true;
        if (!assemblies.TryGetValue("System.Private.CoreLib", out Reference? core))
            throw new DotNetBindingFailure("RSDN1008", "Constraint checking requires locked System.Private.CoreLib metadata.", span);
        var initial = FindType(core, argument, assemblies, budget, span);
        var queue = new Queue<(Reference Reference, TypeDefinitionHandle Type)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(initial);
        for (int count = 0; queue.Count != 0 && count < 256; count++)
        {
            budget.Step(); var current = queue.Dequeue();
            TypeDefinition type = current.Reference.Reader.GetTypeDefinition(current.Type);
            var provider = new SignatureProvider(current.Reference, [], assemblies, budget, span, constraint: true);
            var parents = new List<EntityHandle>();
            if (!type.BaseType.IsNil) parents.Add(type.BaseType);
            foreach (InterfaceImplementationHandle handle in type.GetInterfaceImplementations())
            {
                budget.Step(); parents.Add(current.Reference.Reader.GetInterfaceImplementation(handle).Interface);
            }
            foreach (EntityHandle parent in parents)
            {
                budget.Step(); string name = provider.Entity(parent);
                if (name == expected) return true;
                if (!seen.Add(name) || name.Contains('<', StringComparison.Ordinal)) continue;
                // The supported closed arguments are BCL scalar types; their base/interface closure is in CoreLib.
                queue.Enqueue(FindType(core, name, assemblies, budget, span));
            }
        }
        return false;
    }

    private static Reference LockedAssemblyScope(Reference source, AssemblyReferenceHandle handle,
        Dictionary<string, Reference> assemblies, DotNetBindingBudget budget, TextSpan span)
    {
        budget.Step();
        AssemblyReference identity = source.Reader.GetAssemblyReference(handle);
        string name = source.Reader.GetString(identity.Name);
        if (!assemblies.TryGetValue(name, out Reference? target))
            throw new DotNetBindingFailure("RSDN1008", "Type scope requires an explicitly locked assembly: " + name, span);
        AssemblyDefinition definition = target.Reader.GetAssemblyDefinition();
        if (identity.Version != definition.Version ||
            !StringComparer.Ordinal.Equals(name, target.Reader.GetString(definition.Name)) ||
            !StringComparer.Ordinal.Equals(source.Reader.GetString(identity.Culture), target.Reader.GetString(definition.Culture)) ||
            ((uint)identity.Flags & 0x0E00u) != ((uint)definition.Flags & 0x0E00u) ||
            (identity.Flags & AssemblyFlags.Retargetable) != 0)
            throw new DotNetBindingFailure("RSDN1008", "Assembly scope name/version/culture/content identity disagrees with its lock.", span);
        byte[] declared = source.Reader.GetBlobBytes(identity.PublicKeyOrToken);
        byte[] actual = target.Reader.GetBlobBytes(definition.PublicKey);
        if ((identity.Flags & AssemblyFlags.PublicKey) == 0 && actual.Length != 0)
        {
            // ECMA-335 strong-name tokens require SHA-1. Reference integrity remains the independent SHA-256 PE lock.
#pragma warning disable CA5350
            byte[] digest = SHA1.HashData(actual);
#pragma warning restore CA5350
            actual = new byte[8];
            for (int index = 0; index < 8; index++) { budget.Step(); actual[index] = digest[digest.Length - 1 - index]; }
        }
        if (!declared.AsSpan().SequenceEqual(actual))
            throw new DotNetBindingFailure("RSDN1008", "Assembly scope public key/token disagrees with its locked definition.", span);
        return target;
    }

    private sealed class Reference : IDisposable
    {
        private readonly PEReader _pe;
        public Reference(DotNetReferenceLock locked, DotNetBindingBudget budget, TextSpan span)
        {
            if (string.IsNullOrEmpty(locked.Sha256) || locked.Sha256.Length != 64 || !locked.Sha256.All(Uri.IsHexDigit))
                throw new DotNetBindingFailure("RSDN1008", "A reference requires an explicit SHA-256 lock.", span);
            string path = System.IO.Path.GetFullPath(locked.Path);
            ValidatePath(path, budget, span);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.CancellationToken);
            deadline.CancelAfter(budget.RemainingTime);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Recheck links after opening; immutable SHA-256 still verifies the actual bytes read.
            ValidatePath(path, budget, span);
            if (stream.Length is 0 or > MaximumMetadataBytesPerAssembly)
                throw new DotNetBindingFailure("RSDN1008", "Reference exceeds the 16777216-byte metadata-file bound.", span);
            byte[] bytes = new byte[checked((int)stream.Length)];
            int offset = 0;
            try
            {
                // Each read has the shared caller/deadline token, bounded bytes and operations.
                // Review of the loop exit condition: comparisons only; no assignment in the condition.
                for (int chunk = 0; offset < bytes.Length && chunk < MaximumMetadataBytesPerAssembly; chunk++)
                {
                    budget.Step();
                    int read = stream.ReadAsync(bytes.AsMemory(offset, Math.Min(65_536, bytes.Length - offset)),
                        deadline.Token).AsTask().GetAwaiter().GetResult();
                    budget.Step();
                    if (read == 0) throw new IOException("Reference changed during read.");
                    offset = checked(offset + read);
                }
                if (offset != bytes.Length) throw new IOException("Reference read iteration limit exceeded.");
                budget.Step();
                byte[] growth = new byte[1];
                int trailing = stream.ReadAsync(growth.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
                budget.Step();
                if (trailing != 0) throw new IOException("Reference grew during read.");
            }
            catch (OperationCanceledException) when (!budget.CancellationToken.IsCancellationRequested)
            {
                throw new DotNetBindingFailure("RSDN1001", "Reference read exceeded the shared ten-second bound.", span);
            }
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            budget.Step();
            if (!string.Equals(Sha256, locked.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DotNetBindingFailure("RSDN1008", "Reference hash disagrees with the immutable lock.", span);
            var pe = new PEReader(ImmutableArray.Create(bytes));
            try
            {
                if (!pe.HasMetadata) throw new BadImageFormatException();
                Reader = pe.GetMetadataReader();
                if (!Reader.IsAssembly) throw new BadImageFormatException();
                AssemblyDefinition definition = Reader.GetAssemblyDefinition();
                Name = Reader.GetString(definition.Name); Version = definition.Version.ToString();
                if (Name != locked.AssemblyName || Version != locked.Version)
                    throw new DotNetBindingFailure("RSDN1008", "Reference assembly name/version disagrees with the immutable lock.", span);
                _pe = pe;
            }
            catch { pe.Dispose(); throw; }
        }
        private static void ValidatePath(string path, DotNetBindingBudget budget, TextSpan span)
        {
            FileSystemInfo? part = new FileInfo(path);
            for (int depth = 0; part is not null && depth < 128; depth++)
            {
                budget.Step();
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new DotNetBindingFailure("RSDN1008", "Locked metadata paths cannot traverse links.", span);
                part = part is FileInfo entry ? entry.Directory : ((DirectoryInfo)part).Parent;
            }
            if (part is not null)
                throw new DotNetBindingFailure("RSDN1008", "Locked metadata path depth exceeds 128.", span);
        }
        public MetadataReader Reader { get; }
        public string Name { get; }
        public string Version { get; }
        public string Sha256 { get; }
        public void Dispose() => _pe.Dispose();
    }

    private sealed class SignatureProvider(Reference origin, ImmutableArray<string> arguments,
        Dictionary<string, Reference> assemblies, DotNetBindingBudget budget, TextSpan span,
        bool constraint = false) : ISignatureTypeProvider<string, object?>
    {
        private MetadataReader Reader => origin.Reader;
        private string Rejection => constraint ? "RSDN1006" : "RSDN1005";
        public string Entity(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(Reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => GetTypeFromReference(Reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(Reader, null, (TypeSpecificationHandle)handle, 0),
            _ => throw new BadImageFormatException(),
        };
        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Int32 => "System.Int32", PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.String => "System.String", PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.Object => "System.Object", _ => "unsupported:" + code,
        };
        public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            budget.Step();
            if (!ReferenceEquals(metadata, Reader)) throw new BadImageFormatException();
            return ValidateDefinition(origin, handle, rawTypeKind);
        }
        public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
        {
            budget.Step();
            if (!ReferenceEquals(metadata, Reader)) throw new BadImageFormatException();
            TypeReference type = metadata.GetTypeReference(handle);
            string name = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            Reference scope = type.ResolutionScope.Kind switch
            {
                HandleKind.AssemblyReference => LockedAssemblyScope(origin, (AssemblyReferenceHandle)type.ResolutionScope, assemblies, budget, span),
                HandleKind.ModuleDefinition => origin,
                _ => throw new DotNetBindingFailure("RSDN1008", "Nested, multi-module or unsupported TypeRef scope is outside the explicit assembly lock.", span),
            };
            var resolved = FindType(scope, name, assemblies, budget, span);
            return ValidateDefinition(resolved.Reference, resolved.Type, rawTypeKind);
        }
        private string ValidateDefinition(Reference owner, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            budget.Step();
            TypeDefinition definition = owner.Reader.GetTypeDefinition(handle);
            string name = owner.Reader.GetString(definition.Namespace) + "." + owner.Reader.GetString(definition.Name);
            if (!PublicType(owner.Reader, handle, budget))
                throw new DotNetBindingFailure("RSDN1003", "Resolved boundary type or constraint is inaccessible.", span);
            // BCL primitive/interface constraints must originate in the explicitly locked CoreLib, never a vendor homonym.
            if (name.StartsWith("System.", StringComparison.Ordinal))
            {
                if (owner.Name != "System.Private.CoreLib")
                    throw new DotNetBindingFailure(Rejection, "A BCL type cannot be substituted by a vendor type with the same name.", span);
            }
            else if (name != "InteropFixtures.Counter" || owner.Name != "InteropFixtures")
                throw new DotNetBindingFailure(Rejection, "Named type is outside the frozen metadata boundary.", span);
            bool valueType = false;
            if (!definition.BaseType.IsNil)
            {
                EntityHandle parent = definition.BaseType;
                string parentName;
                Reference parentOwner;
                if (parent.Kind == HandleKind.TypeDefinition)
                {
                    TypeDefinition baseDefinition = owner.Reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                    parentName = owner.Reader.GetString(baseDefinition.Namespace) + "." + owner.Reader.GetString(baseDefinition.Name);
                    parentOwner = owner;
                }
                else if (parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference baseReference = owner.Reader.GetTypeReference((TypeReferenceHandle)parent);
                    parentName = owner.Reader.GetString(baseReference.Namespace) + "." + owner.Reader.GetString(baseReference.Name);
                    parentOwner = baseReference.ResolutionScope.Kind switch
                    {
                        HandleKind.AssemblyReference => LockedAssemblyScope(owner, (AssemblyReferenceHandle)baseReference.ResolutionScope, assemblies, budget, span),
                        HandleKind.ModuleDefinition => owner,
                        _ => throw new DotNetBindingFailure("RSDN1008", "Base type has an unsupported metadata scope.", span),
                    };
                    var actualParent = FindType(parentOwner, parentName, assemblies, budget, span);
                    parentOwner = actualParent.Reference;
                }
                else throw new DotNetBindingFailure(Rejection, "Unsupported base type specification in the frozen boundary.", span);
                valueType = parentOwner.Name == "System.Private.CoreLib" && parentName is "System.ValueType" or "System.Enum";
            }
            if (rawTypeKind is not (0 or 0x11 or 0x12) || rawTypeKind == 0x11 && !valueType || rawTypeKind == 0x12 && valueType)
                throw new DotNetBindingFailure(Rejection, "Metadata class/value shape disagrees with the resolved definition.", span);
            return name;
        }
        public string GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            // Constraint TypeSpecs admit one closed generic interface level only. Validate before decoder recursion.
            TypeSpecification specification = metadata.GetTypeSpecification(handle);
            BlobReader blob = metadata.GetBlobReader(specification.Signature);
            if (blob.Length is 0 or > 512 || blob.ReadByte() != 0x15)
                throw new DotNetBindingFailure("RSDN1006", "Unsupported generic constraint TypeSpec.", span);
            byte kind = blob.ReadByte();
            int genericToken = blob.ReadCompressedInteger();
            int count = blob.ReadCompressedInteger();
            if (kind is not (0x11 or 0x12) || genericToken < 0 || (genericToken & 3) == 2 || count is < 1 or > MaximumGenericArguments)
                throw new DotNetBindingFailure("RSDN1006", "Invalid generic constraint metadata.", span);
            for (int index = 0; index < count; index++)
            {
                byte element = blob.ReadByte();
                if (element is 0x13 or 0x1e)
                {
                    if (blob.ReadCompressedInteger() < 0) throw new BadImageFormatException();
                }
                else if (element is 0x11 or 0x12)
                {
                    int token = blob.ReadCompressedInteger();
                    if (token < 0 || (token & 3) == 2) throw new BadImageFormatException();
                }
                else if (element is not (0x02 or 0x08 or 0x0e or 0x1c))
                    throw new DotNetBindingFailure("RSDN1006", "Nested or unsupported generic constraint argument.", span);
            }
            if (blob.RemainingBytes != 0) throw new BadImageFormatException();
            return specification.DecodeSignature(this, genericContext);
        }
        public string GetGenericMethodParameter(object? genericContext, int index) => index >= 0 && index < arguments.Length ? arguments[index] :
            throw new DotNetBindingFailure("RSDN1006", "Open or invalid method generic argument index.", span);
        public string GetGenericTypeParameter(object? genericContext, int index) =>
            throw new DotNetBindingFailure("RSDN1006", "Open declaring-type generic arguments are excluded.", span);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(',', typeArguments) + ">";
        public string GetArrayType(string elementType, ArrayShape shape) => "unsupported:array";
        public string GetSZArrayType(string elementType) => "unsupported:array";
        public string GetByReferenceType(string elementType) => "unsupported:byref";
        public string GetPointerType(string elementType) => "unsupported:pointer";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "unsupported:callback";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => "unsupported:modifier";
        public string GetPinnedType(string elementType) => "unsupported:pinned";
    }
}
