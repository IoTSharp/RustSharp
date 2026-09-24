using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.CodeGen.IL;

/// <summary>
/// Emits executable PE images from validated CLR LIR, including direct calls
/// between generated static methods and the supported Console.WriteLine overloads.
/// </summary>
public static partial class ClrLirAssemblyEmitter
{
    private const string EmitterIdentity = "RustSharp.CodeGen.IL/ClrLir/0.1.0";

    private const string RuntimeConfig = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "10.0.0"
            }
          }
        }
        """;

    private static readonly Version RuntimeAssemblyVersion = new(10, 0, 0, 0);

    private static readonly ImmutableArray<byte> RuntimePublicKeyToken =
        [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a];

    /// <summary>
    /// Emits an executable PE whose entry point is <paramref name="method"/>.
    /// The currently supported call-site names are <c>Console.WriteLine</c>
    /// and <c>System.Console.WriteLine</c>.
    /// </summary>
    public static GeneratedAssembly Emit(
        ClrLirMethod method,
        string assemblyName,
        RustSharpMetadataDocument? metadataDocument = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        return EmitCore([method], method.Name, assemblyName, null, null, null, default,
            metadataDocument: metadataDocument);
    }

    public static GeneratedAssembly Emit(ClrLirMethod method, string assemblyName,
        IEnumerable<ClrLirValueType> valueTypes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(valueTypes);
        return EmitCore([method], method.Name, assemblyName, null, null, null, default,
            cancellationToken: cancellationToken, valueTypes: valueTypes);
    }

    public static GeneratedAssembly EmitProgram(
        SafeCoreClrResult program,
        string assemblyName,
        string sourceText,
        string sourcePath,
        string pdbFileName,
        ReadOnlyMemory<byte> sourceBytes = default,
        SafeCoreSourceMap? sourceMap = null,
        CancellationToken cancellationToken = default) =>
        EmitProgram(program, assemblyName, sourceText, sourcePath, pdbFileName, sourceBytes, sourceMap,
            metadataDocument: null, cancellationToken: cancellationToken);

    public static GeneratedAssembly EmitProgram(
        SafeCoreClrResult program,
        string assemblyName,
        string sourceText,
        string sourcePath,
        string pdbFileName,
        ReadOnlyMemory<byte> sourceBytes,
        SafeCoreSourceMap? sourceMap,
        RustSharpMetadataDocument? metadataDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!program.IsSuccessful) throw new ArgumentException("A successful lowered program is required.", nameof(program));
        return EmitCore(program.Methods, "Main", assemblyName, sourceText, sourcePath,
            pdbFileName, sourceBytes, program.MethodSpans, sourceMap, metadataDocument,
            program.ValueTypes, program.GenericMetadata, cancellationToken);
    }

    private static GeneratedAssembly EmitCore(
        IReadOnlyList<ClrLirMethod> methods,
        string entryPointName,
        string assemblyName,
        string? sourceText,
        string? sourcePath,
        string? pdbFileName,
        ReadOnlyMemory<byte> sourceBytes,
        IReadOnlyList<TextSpan>? methodSpans = null,
        SafeCoreSourceMap? sourceMap = null,
        RustSharpMetadataDocument? metadataDocument = null,
        IEnumerable<ClrLirValueType>? valueTypes = null,
        ReadOnlyMemory<byte> genericMetadata = default,
        CancellationToken cancellationToken = default)
    {
        var sourceMapClock = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        if (genericMetadata.Length > 8 * 1024 * 1024)
            throw new ArgumentException("Generic metadata exceeds the 8 MiB resource limit.", nameof(genericMetadata));
        byte[] genericResource = genericMetadata.ToArray();
        if (methods.Count is < 1 or > 128) throw new ArgumentException("Expected 1 to 128 methods.", nameof(methods));
        if (sourceMap is not null && sourceMap.Documents.Count is (< 1 or > 1024))
            throw new ArgumentException("Expected 1 to 1024 source documents.", nameof(sourceMap));
        var layouts = new ClrLirValueTypeSet(valueTypes ?? [], CheckSourceMapBudget);
        var definitions = new Dictionary<string, (ClrLirMethod Method, MethodDefinitionHandle Handle)>(StringComparer.Ordinal);
        for (int index = 0; index < methods.Count; index++)
        {
            ClrLirMethod method = methods[index];
            ArgumentNullException.ThrowIfNull(method);
            CheckSourceMapBudget();
            ClrLirValidationResult validation = method.Validate(cancellationToken);
            if (!validation.IsValid)
                throw new InvalidOperationException($"Cannot emit invalid CLR LIR: {string.Join("; ", validation.Diagnostics)}");
            layouts.ValidateMethod(method);
            if (!definitions.TryAdd(method.Name, (method, MetadataTokens.MethodDefinitionHandle(index + 1))))
                throw new ArgumentException("Duplicate method name.", nameof(methods));
        }

        if (!definitions.TryGetValue(entryPointName, out var entry) || entry.Method.Parameters.Length != 0 ||
            entry.Method.ReturnType.Kind is not (ClrLirTypeKind.Void or ClrLirTypeKind.I32))
            throw new ArgumentException("The entry point must have no parameters and return void or i32.", nameof(entryPointName));

        if (assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(assemblyName, Path.GetFileName(assemblyName), StringComparison.Ordinal))
        {
            throw new ArgumentException("The assembly name must be a simple file name.", nameof(assemblyName));
        }

        var metadata = new MetadataBuilder();
        var ilStream = new BlobBuilder();
        var methodBodyStream = new MethodBodyStreamEncoder(ilStream);
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ClrLirMethod method in methods)
            identity.AppendData(CreateModuleVersionId(assemblyName, method, CheckSourceMapBudget).ToByteArray());
        foreach (ClrLirValueType layout in layouts.Definitions)
        {
            CheckSourceMapBudget();
            identity.AppendData(Encoding.UTF8.GetBytes("\0value\0" + layout.Name));
            identity.AppendData(layout.ImplementsMirValue ? "\0mir-accessors\0"u8 : "\0plain-value\0"u8);
            foreach (ClrLirField field in layout.Fields)
                identity.AppendData(Encoding.UTF8.GetBytes("\0" + field.Name + ":" + field.Type));
        }
        if (genericResource.Length != 0)
        {
            identity.AppendData("\0RustSharp.Generics.v1.json\0"u8);
            identity.AppendData(genericResource);
        }
        identity.AppendData(sourceBytes.Span);
        if (metadataDocument is not null)
        {
            byte[] metadataBytes = Encoding.UTF8.GetBytes(metadataDocument.Json);
            Span<byte> encodedLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(encodedLength, metadataBytes.Length);
            identity.AppendData(encodedLength);
            identity.AppendData(metadataBytes);
        }
        if (sourceMap is not null)
        {
            Span<byte> encodedLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(encodedLength, sourceMap.Documents.Count);
            identity.AppendData(encodedLength);
            for (int index = 0; index < sourceMap.Documents.Count; index++)
            {
                CheckSourceMapBudget();
                SafeCoreSourceDocument sourceDocument = sourceMap.Documents[index];
                byte[] pathBytes = Encoding.UTF8.GetBytes(sourceDocument.Path);
                BinaryPrimitives.WriteInt32LittleEndian(encodedLength, pathBytes.Length);
                identity.AppendData(encodedLength);
                identity.AppendData(pathBytes);
                BinaryPrimitives.WriteInt32LittleEndian(encodedLength, sourceDocument.Bytes.Length);
                identity.AppendData(encodedLength);
                identity.AppendData(sourceDocument.Bytes.Span);
            }
        }
        Guid moduleVersionId = new(identity.GetHashAndReset().AsSpan(0, 16));

        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString(assemblyName + ".dll"),
            mvid: metadata.GetOrAddGuid(moduleVersionId),
            encId: default,
            encBaseId: default);
        AssemblyDefinitionHandle assemblyDefinition = metadata.AddAssembly(
            name: metadata.GetOrAddString(assemblyName),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: default,
            hashAlgorithm: AssemblyHashAlgorithm.Sha256);

        BlobHandle runtimePublicKeyToken = metadata.GetOrAddBlob(RuntimePublicKeyToken);
        AssemblyReferenceHandle systemRuntime = AddFrameworkReference(
            metadata,
            "System.Runtime",
            runtimePublicKeyToken);
        AssemblyReferenceHandle systemConsole = AddFrameworkReference(
            metadata,
            "System.Console",
            runtimePublicKeyToken);
        if (metadataDocument is not null)
        {
            AddRustSharpMetadataAttribute(metadata, assemblyDefinition, systemRuntime, metadataDocument.Json);
        }
        TypeReferenceHandle objectType = metadata.AddTypeReference(
            resolutionScope: systemRuntime,
            @namespace: metadata.GetOrAddString("System"),
            name: metadata.GetOrAddString("Object"));
        TypeReferenceHandle consoleType = metadata.AddTypeReference(
            resolutionScope: systemConsole,
            @namespace: metadata.GetOrAddString("System"),
            name: metadata.GetOrAddString("Console"));
        var externalAssemblies = new Dictionary<string, AssemblyReferenceHandle>(StringComparer.Ordinal);
        var externalTypes = new Dictionary<(string Assembly, string Namespace, string Name), TypeReferenceHandle>();

        var valueHandles = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        var constructorHandles = new Dictionary<string, MethodDefinitionHandle>(StringComparer.Ordinal);
        var fieldHandles = new Dictionary<string, ImmutableArray<FieldDefinitionHandle>>(StringComparer.Ordinal);
        int fieldRow = 1;
        int valueMethodRow = methods.Count + 1;
        for (int index = 0; index < layouts.Definitions.Length; index++)
        {
            CheckSourceMapBudget();
            ClrLirValueType layout = layouts.Definitions[index];
            valueHandles.Add(layout.Name, MetadataTokens.TypeDefinitionHandle(index + 3));
            constructorHandles.Add(layout.Name, MetadataTokens.MethodDefinitionHandle(valueMethodRow));
            valueMethodRow += layout.ImplementsMirValue ? 3 : 1;
            var fields = ImmutableArray.CreateBuilder<FieldDefinitionHandle>(layout.Fields.Length);
            for (int fieldIndex = 0; fieldIndex < layout.Fields.Length; fieldIndex++)
                fields.Add(MetadataTokens.FieldDefinitionHandle(fieldRow++));
            fieldHandles.Add(layout.Name, fields.ToImmutable());
        }

        var localSignatures = new List<StandaloneSignatureHandle>();
        foreach (ClrLirMethod method in methods)
        {
            var methodCode = new BlobBuilder();
            var controlFlow = new ControlFlowBuilder();
            var instructionEncoder = new InstructionEncoder(methodCode, controlFlow);
            int maxStack = ClrLirEmitter.EncodeInstructions(
                method,
                metadata,
                site => ResolveCall(metadata, consoleType, definitions, externalAssemblies, externalTypes, site),
                instructionEncoder,
                layout => constructorHandles[layout.Name], (layout, index) => fieldHandles[layout.Name][index],
                CheckSourceMapBudget, type => valueHandles[type.Name!], cancellationToken);
            StandaloneSignatureHandle localSignature = AddLocalSignature(metadata, method.Locals, valueHandles);
            localSignatures.Add(localSignature);
            int methodBodyOffset = methodBodyStream.AddMethodBody(instructionEncoder, maxStack, localSignature);
            MethodAttributes visibility = method.IsPublic
                ? MethodAttributes.Public
                : MethodAttributes.Assembly;
            metadata.AddMethodDefinition(
                attributes: visibility | MethodAttributes.Static | MethodAttributes.HideBySig,
                implAttributes: MethodImplAttributes.IL | MethodImplAttributes.Managed,
                name: metadata.GetOrAddString(method.Name),
                signature: CreateMethodSignature(metadata, method.ReturnType, method.Parameters, valueHandles),
                bodyOffset: methodBodyOffset,
                parameterList: MetadataTokens.ParameterHandle(1));
        }

        MethodDefinitionHandle entryPoint = entry.Handle;
        MethodDefinitionHandle firstMethod = MetadataTokens.MethodDefinitionHandle(1);

        FieldDefinitionHandle firstField = MetadataTokens.FieldDefinitionHandle(1);
        metadata.AddTypeDefinition(
            attributes: TypeAttributes.NotPublic,
            @namespace: default,
            name: metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: firstField,
            methodList: firstMethod);

        metadata.AddTypeDefinition(
            attributes: TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            @namespace: metadata.GetOrAddString("RustSharp.Generated"),
            name: metadata.GetOrAddString("Program"),
            baseType: objectType,
            fieldList: firstField,
            methodList: firstMethod);

        if (!layouts.Definitions.IsEmpty)
        {
            TypeReferenceHandle valueTypeBase = metadata.AddTypeReference(systemRuntime,
                metadata.GetOrAddString("System"), metadata.GetOrAddString("ValueType"));
            int nextFieldRow = 1;
            foreach (ClrLirValueType layout in layouts.Definitions)
            {
                CheckSourceMapBudget();
                FieldDefinitionHandle firstValueField = MetadataTokens.FieldDefinitionHandle(nextFieldRow);
                var constructorCode = new BlobBuilder();
                var constructorEncoder = new InstructionEncoder(constructorCode);
                for (int index = 0; index < layout.Fields.Length; index++)
                {
                    CheckSourceMapBudget();
                    ClrLirField field = layout.Fields[index];
                    var signature = new BlobBuilder();
                    EncodeSignatureType(new BlobEncoder(signature).FieldSignature(), field.Type, valueHandles);
                    FieldDefinitionHandle handle = metadata.AddFieldDefinition(FieldAttributes.Public,
                        metadata.GetOrAddString(field.Name), metadata.GetOrAddBlob(signature));
                    if (handle != fieldHandles[layout.Name][index]) throw new InvalidOperationException("Unstable CLR field order.");
                    nextFieldRow++;
                    constructorEncoder.LoadArgument(0);
                    constructorEncoder.LoadArgument(index + 1);
                    constructorEncoder.OpCode(ILOpCode.Stfld);
                    constructorEncoder.Token(handle);
                }
                constructorEncoder.OpCode(ILOpCode.Ret);
                int bodyOffset = methodBodyStream.AddMethodBody(constructorEncoder, 2);
                MethodDefinitionHandle constructor = metadata.AddMethodDefinition(
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL | MethodImplAttributes.Managed,
                    metadata.GetOrAddString(".ctor"),
                    CreateMethodSignature(metadata, ClrLirType.Void, [.. layout.Fields.Select(static field => field.Type)],
                        valueHandles, isInstanceMethod: true), bodyOffset, MetadataTokens.ParameterHandle(1));
                if (constructor != constructorHandles[layout.Name]) throw new InvalidOperationException("Unstable CLR constructor order.");
                TypeDefinitionHandle handleType = metadata.AddTypeDefinition(
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit,
                    metadata.GetOrAddString("RustSharp.Generated.Values"), metadata.GetOrAddString(layout.Name),
                    valueTypeBase, firstValueField, constructor);
                if (handleType != valueHandles[layout.Name]) throw new InvalidOperationException("Unstable CLR value type order.");
                if (layout.ImplementsMirValue)
                    EmitMirValueAccessors(metadata, methodBodyStream, layout, handleType,
                        constructor, fieldHandles[layout.Name], valueHandles, CheckSourceMapBudget);
            }
        }
        BlobBuilder? managedResources = null;
        if (genericResource.Length != 0)
        {
            CheckSourceMapBudget();
            managedResources = new BlobBuilder();
            managedResources.WriteInt32(genericResource.Length);
            managedResources.WriteBytes(genericResource);
            managedResources.Align(8);
            metadata.AddManifestResource(ManifestResourceAttributes.Public,
                metadata.GetOrAddString("RustSharp.Generics.v1.json"), default, 0);
        }

        byte[]? pdbBytes = null;
        DebugDirectoryBuilder? debugDirectory = null;
        if (sourcePath is not null && sourceText is not null && pdbFileName is not null && methodSpans is not null)
        {
            ReadOnlyMemory<byte> effectiveBytes = sourceBytes.IsEmpty ? Encoding.UTF8.GetBytes(sourceText) : sourceBytes;
            var pdbMetadata = new MetadataBuilder();
            var sourceDocuments = new Dictionary<string, DocumentHandle>(StringComparer.Ordinal);
            DocumentHandle document = default;
            if (sourceMap is null)
            {
                document = AddSourceDocument(sourcePath, effectiveBytes);
            }
            else
            {
                for (int index = 0; index < sourceMap.Documents.Count; index++)
                {
                    CheckSourceMapBudget();
                    SafeCoreSourceDocument sourceDocument = sourceMap.Documents[index];
                    sourceDocuments.Add(sourceDocument.Path, AddSourceDocument(sourceDocument.Path, sourceDocument.Bytes));
                }
            }
            for (int index = 0; index < methods.Count; index++)
            {
                CheckSourceMapBudget();
                string methodSource = sourceText;
                TextSpan methodSpan = methodSpans[index];
                DocumentHandle methodDocument = document;
                if (sourceMap is not null)
                {
                    SafeCoreSourceLocation location = sourceMap.MapSpan(methodSpan);
                    methodSource = location.Document.Text;
                    methodSpan = location.Span;
                    methodDocument = sourceDocuments[location.Document.Path];
                }
                BlobHandle points = IlAssemblyEmitter.CreateSequencePointsBlob(pdbMetadata, methodSource,
                    [new IlAssemblyEmitter.SequencePointData(0, methodSpan)],
                    MetadataTokens.GetRowNumber(localSignatures[index]));
                pdbMetadata.AddMethodDebugInformation(points.IsNil ? default : methodDocument, points);
            }
            foreach (ClrLirValueType layout in layouts.Definitions)
            {
                pdbMetadata.AddMethodDebugInformation(default, default);
                if (layout.ImplementsMirValue)
                {
                    pdbMetadata.AddMethodDebugInformation(default, default);
                    pdbMetadata.AddMethodDebugInformation(default, default);
                }
            }

            var pdbImage = new BlobBuilder();
            BlobContentId pdbId = new PortablePdbBuilder(pdbMetadata, metadata.GetRowCounts(), entryPoint, ComputeContentId).Serialize(pdbImage);
            pdbBytes = pdbImage.ToArray();
            debugDirectory = new DebugDirectoryBuilder();
            debugDirectory.AddCodeViewEntry(pdbFileName, pdbId, 0x0100);

            DocumentHandle AddSourceDocument(string path, ReadOnlyMemory<byte> bytes) => pdbMetadata.AddDocument(
                pdbMetadata.GetOrAddDocumentName(path),
                pdbMetadata.GetOrAddGuid(new Guid("8829d00f-11b8-4213-878b-770e8597ac16")),
                pdbMetadata.GetOrAddBlob(SHA256.HashData(bytes.Span)), default);
        }

        var peHeader = new PEHeaderBuilder(
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware,
            subsystem: Subsystem.WindowsCui);
        var peBuilder = new ManagedPEBuilder(
            peHeader,
            new MetadataRootBuilder(metadata),
            ilStream,
            managedResources: managedResources,
            debugDirectoryBuilder: debugDirectory,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: ComputeContentId);
        var peImage = new BlobBuilder();
        peBuilder.Serialize(peImage);

        return new GeneratedAssembly(peImage.ToArray(), pdbBytes, RuntimeConfig, metadataDocument?.Json)
        {
            RequiresMirRuntime = layouts.Definitions.Any(static layout => layout.ImplementsMirValue) ||
                methods.Any(static method => method.Blocks.Any(static block => block.Instructions.Any(static instruction =>
                    instruction is ClrLirCall { Site.ExternalCall.AssemblyName: "RustSharp.Runtime" }))),
        };

        void CheckSourceMapBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceMapClock.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("CLR assembly emission exceeded its time limit.");
        }
    }

    private static void AddRustSharpMetadataAttribute(
        MetadataBuilder metadata,
        AssemblyDefinitionHandle assembly,
        AssemblyReferenceHandle runtime,
        string json)
    {
        TypeReferenceHandle attributeType = metadata.AddTypeReference(
            resolutionScope: runtime,
            @namespace: metadata.GetOrAddString("System.Reflection"),
            name: metadata.GetOrAddString("AssemblyMetadataAttribute"));
        var signature = new BlobBuilder();
        new BlobEncoder(signature)
            .MethodSignature(isInstanceMethod: true)
            .Parameters(
                parameterCount: 2,
                returnType => returnType.Void(),
                parameters =>
                {
                    parameters.AddParameter().Type().String();
                    parameters.AddParameter().Type().String();
                });
        MemberReferenceHandle constructor = metadata.AddMemberReference(
            attributeType,
            metadata.GetOrAddString(".ctor"),
            metadata.GetOrAddBlob(signature));
        var value = new BlobBuilder();
        value.WriteUInt16(1);
        value.WriteSerializedString(RustSharpMetadataReader.AttributeKey);
        value.WriteSerializedString(json);
        // ECMA-335 II.23.3 requires NumNamed even when it is zero. NativeAOT
        // decodes assembly attributes while rooting reflected value layouts.
        value.WriteUInt16(0);
        metadata.AddCustomAttribute(assembly, constructor, metadata.GetOrAddBlob(value));
    }

    private static EntityHandle ResolveCall(
        MetadataBuilder metadata,
        TypeReferenceHandle consoleType,
        Dictionary<string, (ClrLirMethod Method, MethodDefinitionHandle Handle)> definitions,
        Dictionary<string, AssemblyReferenceHandle> externalAssemblies,
        Dictionary<(string Assembly, string Namespace, string Name), TypeReferenceHandle> externalTypes,
        ClrLirCallSite site)
    {
        if (site.ExternalCall is { } external)
        {
            return AddExternalCallReference(metadata, externalAssemblies, externalTypes, external, site);
        }

        if (definitions.TryGetValue(site.Name, out var definition))
        {
            if (site.ReturnType != definition.Method.ReturnType || !site.ParameterTypes.SequenceEqual(definition.Method.Parameters))
                throw new InvalidOperationException("Generated call signature does not match its target.");
            return definition.Handle;
        }

        return AddCallReference(metadata, consoleType, site);
    }

    private static MemberReferenceHandle AddExternalCallReference(
        MetadataBuilder metadata,
        Dictionary<string, AssemblyReferenceHandle> externalAssemblies,
        Dictionary<(string Assembly, string Namespace, string Name), TypeReferenceHandle> externalTypes,
        ClrLirExternalCall external,
        ClrLirCallSite site)
    {
        if (!externalAssemblies.TryGetValue(external.AssemblyName, out AssemblyReferenceHandle assembly))
        {
            assembly = metadata.AddAssemblyReference(
                name: metadata.GetOrAddString(external.AssemblyName),
                version: external.AssemblyName == "RustSharp.Runtime" ? new Version(0, 1, 0, 0) : new Version(1, 0, 0, 0),
                culture: default,
                publicKeyOrToken: default,
                flags: default,
                hashValue: default);
            externalAssemblies.Add(external.AssemblyName, assembly);
        }

        var typeKey = (external.AssemblyName, external.TypeNamespace, external.TypeName);
        if (!externalTypes.TryGetValue(typeKey, out TypeReferenceHandle type))
        {
            type = metadata.AddTypeReference(
                resolutionScope: assembly,
                @namespace: metadata.GetOrAddString(external.TypeNamespace),
                name: metadata.GetOrAddString(external.TypeName));
            externalTypes.Add(typeKey, type);
        }

        return metadata.AddMemberReference(
            parent: type,
            name: metadata.GetOrAddString(external.MethodName),
            // A value in an imported signature belongs to the producer
            // assembly.  Encoding it with the consumer's local value handles
            // would create a same-named but different CLR type and makes the
            // MemberRef unverifiable at runtime.  Resolve every aggregate in
            // this signature through a TypeRef scoped to the producer.
            signature: CreateMethodSignature(metadata, site.ReturnType, site.ParameterTypes,
                externalValueResolver: name => AddExternalValueTypeReference(
                    metadata, externalAssemblies, externalTypes, external, name)));
    }

    private static TypeReferenceHandle AddExternalValueTypeReference(
        MetadataBuilder metadata,
        Dictionary<string, AssemblyReferenceHandle> externalAssemblies,
        Dictionary<(string Assembly, string Namespace, string Name), TypeReferenceHandle> externalTypes,
        ClrLirExternalCall external,
        string name)
    {
        if (!externalAssemblies.TryGetValue(external.AssemblyName, out AssemblyReferenceHandle assembly))
        {
            assembly = metadata.AddAssemblyReference(
                name: metadata.GetOrAddString(external.AssemblyName),
                version: new Version(1, 0, 0, 0),
                culture: default,
                publicKeyOrToken: default,
                flags: default,
                hashValue: default);
            externalAssemblies.Add(external.AssemblyName, assembly);
        }

        var key = (external.AssemblyName, "RustSharp.Generated.Values", name);
        if (!externalTypes.TryGetValue(key, out TypeReferenceHandle type))
        {
            type = metadata.AddTypeReference(
                resolutionScope: assembly,
                @namespace: metadata.GetOrAddString(key.Item2),
                name: metadata.GetOrAddString(name));
            externalTypes.Add(key, type);
        }

        return type;
    }

    private static AssemblyReferenceHandle AddFrameworkReference(
        MetadataBuilder metadata,
        string name,
        BlobHandle publicKeyToken) =>
        metadata.AddAssemblyReference(
            name: metadata.GetOrAddString(name),
            version: RuntimeAssemblyVersion,
            culture: default,
            publicKeyOrToken: publicKeyToken,
            flags: default,
            hashValue: default);

    private static MemberReferenceHandle AddCallReference(
        MetadataBuilder metadata,
        TypeReferenceHandle consoleType,
        ClrLirCallSite site)
    {
        if (!string.Equals(site.Name, "Console.WriteLine", StringComparison.Ordinal) &&
            !string.Equals(site.Name, "System.Console.WriteLine", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The CLR LIR PE spike does not support call site '{site.Name}'.");
        }

        if (site.ReturnType != ClrLirType.Void || site.ParameterTypes.Length != 1 ||
            site.ParameterTypes[0].Kind is not (ClrLirTypeKind.I32 or ClrLirTypeKind.Bool or ClrLirTypeKind.Text or ClrLirTypeKind.Any))
        {
            throw new NotSupportedException(
                "The CLR LIR PE spike supports only one-argument void Console.WriteLine overloads.");
        }

        return metadata.AddMemberReference(
            parent: consoleType,
            name: metadata.GetOrAddString("WriteLine"),
            signature: CreateMethodSignature(metadata, site.ReturnType, site.ParameterTypes));
    }

    private static BlobHandle CreateMethodSignature(
        MetadataBuilder metadata,
        ClrLirType methodReturnType,
        IReadOnlyList<ClrLirType> parameterTypes,
        IReadOnlyDictionary<string, TypeDefinitionHandle>? valueHandles = null,
        bool isInstanceMethod = false,
        Func<string, EntityHandle>? externalValueResolver = null)
    {
        var signature = new BlobBuilder();
        new BlobEncoder(signature)
            .MethodSignature(isInstanceMethod: isInstanceMethod)
            .Parameters(
                parameterCount: parameterTypes.Count,
                returnTypeEncoder => EncodeReturnType(returnTypeEncoder, methodReturnType, valueHandles, externalValueResolver),
                parameters =>
                {
                    foreach (ClrLirType parameterType in parameterTypes)
                    {
                        EncodeParameterType(parameters.AddParameter().Type(
                                isByRef: parameterType.Kind == ClrLirTypeKind.ByReference), parameterType,
                            valueHandles, externalValueResolver);
                    }
                });

        return metadata.GetOrAddBlob(signature);
    }

    private static void EncodeReturnType(
        System.Reflection.Metadata.Ecma335.ReturnTypeEncoder encoder,
        ClrLirType type,
        IReadOnlyDictionary<string, TypeDefinitionHandle>? valueHandles,
        Func<string, EntityHandle>? externalValueResolver = null)
    {
        if (type == ClrLirType.Void)
        {
            encoder.Void();
            return;
        }

        if (type.Kind == ClrLirTypeKind.ByReference)
        {
            if (!type.TryGetByReferenceElement(out ClrLirType element))
                throw new ArgumentException("Invalid by-reference return type.", nameof(type));
            EncodeSignatureType(encoder.Type(isByRef: true), element, valueHandles, externalValueResolver);
            return;
        }

        EncodeSignatureType(encoder.Type(isByRef: false), type, valueHandles, externalValueResolver);
    }

    private static void EncodeParameterType(
        System.Reflection.Metadata.Ecma335.SignatureTypeEncoder encoder,
        ClrLirType type,
        IReadOnlyDictionary<string, TypeDefinitionHandle>? valueHandles,
        Func<string, EntityHandle>? externalValueResolver = null)
    {
        if (type == ClrLirType.Void)
        {
            throw new ArgumentException("Void is not a valid parameter type.", nameof(type));
        }

        if (type.Kind == ClrLirTypeKind.ByReference)
        {
            if (!type.TryGetByReferenceElement(out ClrLirType element))
                throw new ArgumentException("Invalid by-reference parameter type.", nameof(type));
            EncodeSignatureType(encoder, element, valueHandles, externalValueResolver);
            return;
        }

        EncodeSignatureType(encoder, type, valueHandles, externalValueResolver);
    }

    private static void EncodeSignatureType(
        System.Reflection.Metadata.Ecma335.SignatureTypeEncoder encoder,
        ClrLirType type,
        IReadOnlyDictionary<string, TypeDefinitionHandle>? valueHandles,
        Func<string, EntityHandle>? externalValueResolver = null)
    {
        switch (type.Kind)
        {
            case ClrLirTypeKind.I32:
                encoder.Int32();
                break;
            case ClrLirTypeKind.Bool:
                encoder.Boolean();
                break;
            case ClrLirTypeKind.Text:
                encoder.String();
                break;
            case ClrLirTypeKind.Any:
                encoder.Object();
                break;
            case ClrLirTypeKind.Value when type.Name is not null && valueHandles is not null && valueHandles.TryGetValue(type.Name, out TypeDefinitionHandle handle):
                encoder.Type(handle, isValueType: true);
                break;
            case ClrLirTypeKind.Value when type.Name is not null && externalValueResolver is not null:
                encoder.Type(externalValueResolver(type.Name), isValueType: true);
                break;
            case ClrLirTypeKind.ByReference:
                throw new ArgumentException("By-reference encoding requires a parameter or return signature encoder.", nameof(type));
            default:
                throw new ArgumentException($"Unsupported CLR LIR signature type '{type}'.", nameof(type));
        }
    }

    private static StandaloneSignatureHandle AddLocalSignature(
        MetadataBuilder metadata,
        ImmutableArray<ClrLirLocal> locals,
        IReadOnlyDictionary<string, TypeDefinitionHandle> valueHandles)
    {
        if (locals.IsEmpty)
        {
            return default;
        }

        var signature = new BlobBuilder();
        LocalVariablesEncoder variables = new BlobEncoder(signature).LocalVariableSignature(locals.Length);
        foreach (ClrLirLocal local in locals)
        {
            if (local.Type == ClrLirType.Void)
            {
                throw new ArgumentException("Void is not a valid local type.", nameof(locals));
            }
            if (local.Type.Kind == ClrLirTypeKind.ByReference)
            {
                if (!local.Type.TryGetByReferenceElement(out ClrLirType element))
                    throw new ArgumentException("Invalid managed by-reference local.", nameof(locals));
                EncodeSignatureType(variables.AddVariable().Type(isByRef: true, isPinned: false), element, valueHandles);
                continue;
            }

            EncodeSignatureType(variables.AddVariable().Type(isByRef: false, isPinned: false), local.Type, valueHandles);
        }

        return metadata.AddStandaloneSignature(metadata.GetOrAddBlob(signature));
    }

    private static Guid CreateModuleVersionId(string assemblyName, ClrLirMethod method, Action checkBudget)
    {
        var descriptor = new StringBuilder(EmitterIdentity)
            .Append('\0')
            .Append(assemblyName)
            .Append('\0')
            .Append(method.Name)
            .Append('\0')
            .Append(method.ReturnType);
        _ = descriptor.Append('\0').Append("params");
        foreach (ClrLirType parameter in method.Parameters)
        {
            _ = descriptor.Append('\0').Append(parameter);
        }

        _ = descriptor.Append('\0').Append("locals");
        foreach (ClrLirLocal local in method.Locals)
        {
            _ = descriptor.Append('\0').Append(local.Name).Append(':').Append(local.Type);
        }

        foreach (ClrLirBlock block in method.Blocks)
        {
            _ = descriptor.Append('\0').Append(block.Label);
            foreach (ClrLirInstruction instruction in block.Instructions)
            {
                checkBudget();
                _ = descriptor.Append('\0').Append(instruction.GetType().Name);
                switch (instruction)
                {
                    case ClrLirBox box:
                        descriptor.Append(box.Type);
                        break;
                    case ClrLirLoadType loadType:
                        descriptor.Append(loadType.Type);
                        break;
                    case ClrLirUnbox unbox:
                        descriptor.Append(unbox.Type);
                        break;
                    case ClrLirLoadInt32 loadInt32:
                        _ = descriptor.Append(':').Append(loadInt32.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case ClrLirLoadBoolean loadBoolean:
                        _ = descriptor.Append(':').Append(loadBoolean.Value ? '1' : '0');
                        break;
                    case ClrLirLoadString loadString:
                        _ = descriptor.Append(':').Append(loadString.Value);
                        break;
                    case ClrLirLoadLocal loadLocal:
                        _ = descriptor.Append(':').Append(loadLocal.Index);
                        break;
                    case ClrLirLoadLocalAddress address:
                        descriptor.Append(address.Index).Append(':').Append(address.IsMutable);
                        break;
                    case ClrLirFieldAddress address:
                        descriptor.Append(address.Definition.Name).Append(':').Append(address.FieldIndex).Append(':').Append(address.IsMutable);
                        break;
                    case ClrLirLoadIndirect load:
                        descriptor.Append(load.Type);
                        break;
                    case ClrLirStoreIndirect store:
                        descriptor.Append(store.Type);
                        break;
                    case ClrLirReadOnlyReference readOnly:
                        descriptor.Append(readOnly.ElementType);
                        break;
                    case ClrLirStoreLocal storeLocal:
                        _ = descriptor.Append(':').Append(storeLocal.Index);
                        break;
                    case ClrLirLoadArgument argument:
                        _ = descriptor.Append(':').Append(argument.Index);
                        break;
                    case ClrLirDiscard discard:
                        _ = descriptor.Append(':').Append(discard.Type);
                        break;
                    case ClrLirConstructValue construct:
                        _ = descriptor.Append(':').Append(construct.Definition.Name);
                        break;
                    case ClrLirReadField field:
                        _ = descriptor.Append(':').Append(field.Definition.Name).Append(':').Append(field.FieldIndex);
                        break;
                    case ClrLirBinary binary:
                        _ = descriptor.Append(':').Append(binary.Operator).Append(':').Append(binary.OperandType.Kind);
                        break;
                    case ClrLirCall call:
                        _ = descriptor.Append(':').Append(call.Site.Name).Append(':').Append(call.Site.ReturnType);
                        if (call.Site.ExternalCall is { } external)
                        {
                            _ = descriptor.Append(":external:").Append(external.AssemblyName)
                                .Append(':').Append(external.TypeNamespace)
                                .Append(':').Append(external.TypeName)
                                .Append(':').Append(external.MethodName);
                            if (external.PanicStrategy is { } panic)
                                _ = descriptor.Append(":panic:").Append(panic);
                            if (external.ReturnContract is { } resultContract)
                                _ = descriptor.Append(":return:").Append(resultContract);
                            foreach (string parameterContract in external.ParameterContracts)
                                _ = descriptor.Append(":param:").Append(parameterContract);
                        }
                        foreach (ClrLirType parameterType in call.Site.ParameterTypes)
                        {
                            _ = descriptor.Append(':').Append(parameterType);
                        }

                        break;
                    case ClrLirBranch branch:
                        _ = descriptor.Append(':').Append(branch.Target);
                        break;
                    case ClrLirLeave leave:
                        _ = descriptor.Append(':').Append(leave.Target);
                        break;
                    case ClrLirBranchTrue branchTrue:
                        _ = descriptor.Append(':').Append(branchTrue.Target);
                        break;
                }
            }
        }

        if (!method.ExceptionCleanup.IsEmpty)
        {
            _ = descriptor.Append('\0').Append("fault-cleanup");
            foreach (ClrLirCallSite cleanup in method.ExceptionCleanup)
            {
                _ = descriptor.Append('\0').Append(cleanup.Name).Append(':').Append(cleanup.ReturnType);
                foreach (ClrLirType parameter in cleanup.ParameterTypes)
                    _ = descriptor.Append(':').Append(parameter);
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.ToString()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static BlobContentId ComputeContentId(IEnumerable<Blob> content)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (Blob blob in content)
        {
            hash.AppendData(blob.GetBytes());
        }

        return BlobContentId.FromHash(hash.GetHashAndReset());
    }
}
