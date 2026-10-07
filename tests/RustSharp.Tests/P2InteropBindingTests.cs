using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class P2InteropBindingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2 interop binding import-static-add", StaticAddAsync),
        new("P2 interop binding import-missing-identity", MissingIdentityAsync),
        new("P2 interop binding import-duplicate-alias", DuplicateAliasAsync),
        new("P2 interop binding overload-exact-i32", ExactOverloadAsync),
        new("P2 interop binding overload-ambiguous", AmbiguousAsync),
        new("P2 interop binding overload-wrong-argument", WrongArgumentAsync),
        new("P2 interop binding generic-closed-identity", ClosedGenericAsync),
        new("P2 interop binding generic-invalid-constraint", InvalidConstraintAsync),
        new("P2 interop binding generic-open-argument", OpenGenericAsync),
        new("P2 interop binding visibility-public-static", PublicStaticAsync),
        new("P2 interop binding visibility-private-member", PrivateMemberAsync),
        new("P2 interop binding visibility-instance-without-adapter", InstanceAsync),
        new("P2 interop binding exclusion-unmanaged-abi", UnmanagedAsync),
        new("P2 interop binding exclusion-unlisted-member", UnlistedAsync),
    ];

    private const string I32Add = "System.Int32(System.Int32,System.Int32)";
    private static string Source(string member = "Add", string? signature = I32Add,
        string parameters = "left: i32, right: i32", string result = "i32", string type = "InteropFixtures.Math",
        string assembly = "InteropFixtures", string alias = "add") =>
        "#[dotnet_import(assembly = \"" + assembly + "\", type = \"" + type + "\", member = \"" + member + "\"" +
        (signature is null ? "" : ", signature = \"" + signature + "\"") + ")] extern \"dotnet\" { pub fn " + alias +
        "(" + parameters + ") -> " + result + "; }";

    private static Task StaticAddAsync()
    {
        DotNetImportBindingResult first = Bind(Source());
        DotNetImportBindingResult second = Bind("// import comment\n" + Source());
        AssertEx.True(first.IsSuccessful, Explain(first)); AssertEx.True(second.IsSuccessful, Explain(second));
        DotNetBoundImport bound = first.Imports.Single();
        AssertEx.Equal("fixture-add-i32", bound.MemberContractId);
        AssertEx.Equal(bound.MethodToken, second.Imports.Single().MethodToken);
        AssertEx.Equal(bound.ReferenceSha256, second.Imports.Single().ReferenceSha256);
        AssertEx.True(bound.ModuleVersionId != Guid.Empty, "Actual reference MVID is retained.");
        AssertEx.False(first.RuntimeEvidence, "Binding cannot claim generated IL or runtime execution.");
        // A missing/corrupted reference lock must reject; no host reflection discovery fallback.
        var corrupt = Locks().SetItem(0, Locks()[0] with { Sha256 = new string('0', 64) });
        Reject(Source(), "RSDN1008", corrupt);
        Reject(Source(), "RSDN1008", []);
        BoundMemberControls();
        ScopeMetadataControls();
        VerifyFrozenLeafMap();
        return Task.CompletedTask;
    }
    private static Task MissingIdentityAsync()
    {
        Reject(Source().Replace("assembly = \"InteropFixtures\", ", "", StringComparison.Ordinal), "RSDN1001");
        Reject(Source().Replace("type = \"InteropFixtures.Math\"", "type = \"Bad..Name\"", StringComparison.Ordinal), "RSDN1001");
        Reject(new string(' ', 262145), "RSDN1001");
        string nested = string.Concat(Enumerable.Repeat("Option<", 40)) + "dotnet::Object<InteropFixtures::Counter>" +
            string.Concat(Enumerable.Repeat(" >", 40));
        Reject(Source(parameters: "left: " + nested + ", right: i32"), "RSDN1005");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => DotNetImportBinding.Bind(Source(), "binding.rs", Locks(), cancellation.Token));
        return Task.CompletedTask;
    }
    private static Task DuplicateAliasAsync()
    { Reject(Source() + "\n" + Source(alias: "add"), "RSDN1002"); return Task.CompletedTask; }
    private static Task ExactOverloadAsync()
    {
        DotNetImportBindingResult result = Bind(Source()); AssertEx.True(result.IsSuccessful, Explain(result));
        AssertEx.Equal(I32Add, result.Imports.Single().ClosedSignature);
        return Task.CompletedTask;
    }
    private static Task AmbiguousAsync()
    { Reject(Source(signature: null), "RSDN1004"); return Task.CompletedTask; }
    private static Task WrongArgumentAsync()
    { Reject(Source(parameters: "left: bool, right: i32"), "RSDN1005"); return Task.CompletedTask; }
    private static Task ClosedGenericAsync()
    {
        DotNetImportBindingResult result = Bind(Source("Identity<System.Int32>", "System.Int32(System.Int32)", "value: i32", "i32", "InteropFixtures.Algorithms"));
        AssertEx.True(result.IsSuccessful, Explain(result));
        AssertEx.Equal("fixture-identity-i32", result.Imports.Single().MemberContractId);
        AssertEx.Equal("System.Int32", result.Imports.Single().GenericArguments.Single());
        // This checks the actual fixture independently. P2-06.03 must execute emitted import lowering.
        AssertEx.Equal(42, InteropFixtures.Algorithms.Identity(42));
        return Task.CompletedTask;
    }
    private static Task InvalidConstraintAsync()
    {
        Reject(Source("Identity<System.String>", "System.String(System.String)", "value: dotnet::String", "dotnet::String", "InteropFixtures.Algorithms"), "RSDN1006");
        return Task.CompletedTask;
    }
    private static Task OpenGenericAsync()
    {
        Reject(Source("Identity<T>", "System.Int32(System.Int32)", "value: i32", "i32", "InteropFixtures.Algorithms"), "RSDN1006");
        Reject(Source("Identity", "System.Int32(System.Int32)", "value: i32", "i32", "InteropFixtures.Algorithms"), "RSDN1006");
        return Task.CompletedTask;
    }
    private static Task PublicStaticAsync()
    {
        DotNetImportBindingResult result = Bind(Source()); AssertEx.True(result.IsSuccessful, Explain(result));
        using var pe = new PEReader(File.OpenRead(FixturePath));
        MetadataReader metadata = pe.GetMetadataReader();
        var handle = System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(result.Imports.Single().MethodToken & 0x00ffffff);
        var method = metadata.GetMethodDefinition(handle);
        AssertEx.Equal(System.Reflection.MethodAttributes.Public, method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask);
        AssertEx.True((method.Attributes & System.Reflection.MethodAttributes.Static) != 0, "Actual fixture method is static.");
        return Task.CompletedTask;
    }
    private static Task PrivateMemberAsync()
    { Reject(Source("Hidden", "System.Int32(System.Int32)", "value: i32"), "RSDN1003"); return Task.CompletedTask; }
    private static Task InstanceAsync()
    { Reject(Source("ReadInstance", "System.Int32()", "", "i32", "InteropFixtures.Counter"), "RSDN1010"); return Task.CompletedTask; }
    private static Task UnmanagedAsync()
    {
        Reject(Source().Replace("extern \"dotnet\"", "extern \"C\"", StringComparison.Ordinal), "RSDN1010");
        Reject(Source(parameters: "left: *const i32, right: i32"), "RSDN1010");
        return Task.CompletedTask;
    }
    private static Task UnlistedAsync()
    {
        Reject(Source("Unlisted", "System.Int32(System.Int32)", "value: i32"), "RSDN1005");
        Reject(Source(signature: "System.Boolean(System.Boolean,System.Boolean)", parameters: "left: bool, right: bool", result: "bool"), "RSDN1005");
        return Task.CompletedTask;
    }

    private static DotNetImportBindingResult Bind(string source) => DotNetImportBinding.Bind(source, "binding.rs", Locks());
    private static string Explain(DotNetImportBindingResult result) => string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message));
    private static void Reject(string source, string code, IReadOnlyList<DotNetReferenceLock>? references = null)
    {
        DotNetImportBindingResult result = DotNetImportBinding.Bind(source, "binding.rs", references ?? Locks());
        AssertEx.False(result.IsSuccessful, "Negative source/reference must reject."); AssertEx.Equal(0, result.Imports.Length);
        var diagnostic = result.Diagnostics.Single(); AssertEx.Equal(code, diagnostic.Code, Explain(result));
        AssertEx.Equal("binding.rs", diagnostic.SourcePath!);
        AssertEx.True(diagnostic.Span.Start >= 0 && diagnostic.Span.End <= source.Length, "Exact source span must remain in source bounds.");
        if (source.Length < 262144) AssertEx.True(diagnostic.Span.Length > 0, "Semantic rejection must identify a real source span.");
    }
    private static string CoreLibraryPath => Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Private.CoreLib.dll");
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "InteropFixtures.dll");
    private static ImmutableArray<DotNetReferenceLock> Locks() =>
    [Lock(FixturePath), Lock(CoreLibraryPath),
        Lock(Path.Combine(Path.GetDirectoryName(CoreLibraryPath)!, "System.Runtime.dll"))];
    private static DotNetReferenceLock Lock(string path)
    {
        using var stream = File.OpenRead(path); AssertEx.True(stream.Length <= DotNetImportBinding.MaximumMetadataBytesPerAssembly, "Reference is bounded.");
        string hash = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0;
        using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader(); var assembly = metadata.GetAssemblyDefinition();
        return new(path, metadata.GetString(assembly.Name), assembly.Version.ToString(), hash);
    }
    private static void VerifyFrozenLeafMap()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string path = Path.Combine(root, "tools/RustSharp.Conformance/fixtures/p2-dotnet-interop-v1-manifest.json");
        string manifestText = File.ReadAllText(path);
        DotNetInteropContractValidation frozen = DotNetInteropContract.Validate(manifestText);
        AssertEx.True(frozen.IsValid, string.Join("; ", frozen.Issues));
        using var document = JsonDocument.Parse(manifestText);
        AssertEx.Equal(36, document.RootElement.GetProperty("denominator").GetInt32());
        AssertEx.Equal(36, document.RootElement.GetProperty("cases").GetArrayLength());
        string[] leafIds = document.RootElement.GetProperty("cases").EnumerateArray().Where(item => item.GetProperty("leaf").GetString() == "P2-06.02")
            .Select(item => item.GetProperty("id").GetString()!).Order(StringComparer.Ordinal).ToArray();
        string[] tests = All.Select(test => test.Name["P2 interop binding ".Length..]).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(14, leafIds.Length); AssertEx.Equal(string.Join('|', leafIds), string.Join('|', tests));
    }

    private static void BoundMemberControls()
    {
        // Real locked BCL forwarding and separate fixture PE metadata: no imported-call execution claim.
        string[] sources =
        [
            Source("Abs", "System.Int32(System.Int32)", "value: i32", "i32", "System.Math", "System.Runtime"),
            Source("Read", "System.Int32(InteropFixtures.Counter)", "counter: &dotnet::Object<InteropFixtures::Counter>",
                "i32", "InteropFixtures.CounterAdapters"),
            Source("Release", "System.Void(InteropFixtures.Counter)", "counter: dotnet::Object<InteropFixtures::Counter>",
                "()", "InteropFixtures.CounterAdapters"),
        ];
        string[] contracts = ["bcl-abs-i32", "fixture-counter-read", "fixture-counter-release"];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < 3; index++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(15), "Three positive metadata controls share a 15-second bound.");
            DotNetImportBindingResult bound = DotNetImportBinding.Bind(sources[index], "binding.rs", Locks(), cancellation.Token);
            AssertEx.True(bound.IsSuccessful, Explain(bound));
            AssertEx.Equal(contracts[index], bound.Imports.Single().MemberContractId);
            AssertEx.False(bound.RuntimeEvidence, "Positive metadata controls do not prove emitted imported calls.");
        }
    }
    // Physical independent PE controls. Nothing in these generated assemblies is loaded or executed.
    // The fixed import-static-add registration retains its identity and denominator.
    private static void ScopeMetadataControls()
    {
        string temporaryParent = Path.GetFullPath(Path.GetTempPath());
        string owned = Path.GetFullPath(Path.Combine(temporaryParent, "rustsharp-interop-scope-" + Guid.NewGuid().ToString("N")));
        var files = new List<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = Stopwatch.StartNew();
        void Guard()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(15) && files.Count <= 12, "PE scope controls have fixed file and wall-clock limits.");
        }
        string Write(string name, byte[] bytes)
        {
            Guard(); string path = Path.GetFullPath(Path.Combine(owned, name));
            AssertEx.True(path.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.Ordinal), "PE fixture is inside the unique owned directory.");
            AssertEx.True(files.Count < 12, "No more than 12 PE control files may be created.");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // CreateNew establishes ownership before registration; cancellation still triggers owned cleanup.
            files.Add(path);
            stream.WriteAsync(bytes.AsMemory(), cancellation.Token).AsTask().GetAwaiter().GetResult();
            Guard(); return path;
        }
        bool created = false;
        Exception? primaryFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            Guard();
            AssertEx.False(Directory.Exists(owned), "The uniquely named control directory must not already exist.");
            Directory.CreateDirectory(owned); created = true;
            string vendor = Write("Vendor.dll", ScopePe("Vendor", "none"));
            string[] faults = ["homonym", "version", "culture", "token"];
            for (int index = 0; index < faults.Length; index++)
            {
                Guard(); string fault = faults[index];
                string fixture = Write("InteropFixtures-" + fault + ".dll", ScopePe("InteropFixtures", fault));
                DotNetImportBindingResult rejected = DotNetImportBinding.Bind(Source(), "binding.rs",
                    new[] { Lock(fixture), Lock(vendor) }, cancellation.Token);
                AssertEx.False(rejected.IsSuccessful, "A physical PE vendor homonym or mismatched assembly scope must reject.");
                AssertEx.Equal(0, rejected.Imports.Length);
                AssertEx.Equal(fault == "homonym" ? "RSDN1005" : "RSDN1008", rejected.Diagnostics.Single().Code);
            }
            string adapter = Write("RustSharp.Interop.Adapters.dll", ScopePe("RustSharp.Interop.Adapters", "adapter"));
            DotNetImportBindingResult adapterBound = DotNetImportBinding.Bind(Source("Length", "System.Int32(System.String)",
                "value: dotnet::String", "i32", "RustSharp.Interop.Adapters.StringSegmentAdapters", "RustSharp.Interop.Adapters"),
                "binding.rs", new[] { Lock(adapter) }, cancellation.Token);
            AssertEx.True(adapterBound.IsSuccessful, Explain(adapterBound));
            AssertEx.Equal("nuget-stringsegment-length", adapterBound.Imports.Single().MemberContractId);
            AssertEx.False(adapterBound.RuntimeEvidence, "Synthetic adapter metadata is not package, AOT or runtime proof.");
            string shaped = Write("InteropFixtures-shape.dll", ScopePe("InteropFixtures", "value-shape"));
            DotNetImportBindingResult wrongShape = DotNetImportBinding.Bind(Source("Read", "System.Int32(InteropFixtures.Counter)",
                "counter: &dotnet::Object<InteropFixtures::Counter>", "i32", "InteropFixtures.CounterAdapters"), "binding.rs",
                new[] { Lock(shaped), Lock(CoreLibraryPath),
                    Lock(Path.Combine(Path.GetDirectoryName(CoreLibraryPath)!, "System.Runtime.dll")) }, cancellation.Token);
            AssertEx.False(wrongShape.IsSuccessful, "A class signature referring to an actual value definition rejects.");
            AssertEx.Equal("RSDN1005", wrongShape.Diagnostics.Single().Code);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
        }
        finally
        {
            if (created)
            {
                try
                {
                    var cleanup = Stopwatch.StartNew();
                    AssertEx.True(owned.StartsWith(temporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal), "Cleanup retains the verified temporary parent.");
                    foreach (string file in files)
                    {
                        AssertEx.True(cleanup.Elapsed < TimeSpan.FromSeconds(10) && files.Count <= 12 &&
                            Path.GetFullPath(file).StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Cleanup deletes only bounded owned files.");
                        File.Delete(file);
                    }
                    Directory.Delete(owned); AssertEx.False(Directory.Exists(owned), "PE scope-control temporary root is reclaimed.");
                }
                catch (Exception failure)
                {
                    cleanupFailure = failure;
                }
            }
        }
        if (cleanupFailure is not null)
            throw new AggregateException("PE control failed or owned cleanup failed.",
                primaryFailure is null ? [cleanupFailure] : [primaryFailure, cleanupFailure]);
        if (primaryFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    private static byte[] ScopePe(string assemblyName, string fault)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(assemblyName + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assemblyName), new Version(1, 0, 0, 0), default, default, (AssemblyFlags)0, default);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        if (assemblyName == "Vendor")
        {
            metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("System"), metadata.GetOrAddString("Int32"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        }
        var signature = new BlobBuilder();
        string owner = "Math", member = "Add";
        if (assemblyName != "Vendor" && fault == "value-shape")
        {
            string runtimePath = Path.Combine(Path.GetDirectoryName(CoreLibraryPath)!, "System.Runtime.dll");
            using var runtimeStream = File.OpenRead(runtimePath); using var runtimePe = new PEReader(runtimeStream);
            MetadataReader reader = runtimePe.GetMetadataReader(); AssemblyDefinition runtime = reader.GetAssemblyDefinition();
            AssemblyReferenceHandle scope = metadata.AddAssemblyReference(metadata.GetOrAddString(reader.GetString(runtime.Name)),
                runtime.Version, metadata.GetOrAddString(reader.GetString(runtime.Culture)), metadata.GetOrAddBlob(reader.GetBlobBytes(runtime.PublicKey)),
                AssemblyFlags.PublicKey, default);
            TypeReferenceHandle valueType = metadata.AddTypeReference(scope, metadata.GetOrAddString("System"), metadata.GetOrAddString("ValueType"));
            TypeDefinitionHandle counter = metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
                metadata.GetOrAddString("InteropFixtures"), metadata.GetOrAddString("Counter"), valueType,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            // Deliberately CLASS, while the resolved Counter really derives from CoreLib ValueType.
            signature.WriteBytes(new byte[] { 0, 1, 0x08, 0x12, checked((byte)(MetadataTokens.GetRowNumber(counter) << 2)) });
            owner = "CounterAdapters"; member = "Read";
        }
        else if (assemblyName == "RustSharp.Interop.Adapters")
        {
            owner = "StringSegmentAdapters"; member = "Length";
            signature.WriteBytes(new byte[] { 0, 1, 0x08, 0x0e });
        }
        else if (assemblyName != "Vendor")
        {
            AssemblyReferenceHandle scope = metadata.AddAssemblyReference(metadata.GetOrAddString("Vendor"),
                fault == "version" ? new Version(2, 0, 0, 0) : new Version(1, 0, 0, 0),
                fault == "culture" ? metadata.GetOrAddString("en-US") : default,
                fault == "token" ? metadata.GetOrAddBlob(new byte[8]) : default, (AssemblyFlags)0, default);
            TypeReferenceHandle fake = metadata.AddTypeReference(scope, metadata.GetOrAddString("System"), metadata.GetOrAddString("Int32"));
            byte token = checked((byte)((MetadataTokens.GetRowNumber(fake) << 2) | 1));
            signature.WriteBytes(new byte[] { 0, 2, 0x12, token, 0x12, token, 0x12, token });
        }
        var il = new BlobBuilder(); il.WriteInt32(0);
        if (assemblyName != "Vendor")
        {
            metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString(assemblyName == "RustSharp.Interop.Adapters" ? assemblyName : "InteropFixtures"), metadata.GetOrAddString(owner), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            // Valid tiny IL body: ldnull/ret for the synthetic reference, or ldc.i4.0/ret for Read. Never invoked.
            il.WriteBytes(new byte[] { 0x0A, fault is "value-shape" or "adapter" ? (byte)0x16 : (byte)0x14, 0x2A });
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString(member), metadata.GetOrAddBlob(signature), 4, MetadataTokens.ParameterHandle(1));
        }
        var builder = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), il);
        var output = new BlobBuilder(); builder.Serialize(output); return output.ToArray();
    }
}
