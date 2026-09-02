using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace RustSharp.CodeGen.IL;

/// <summary>
/// Emits a small executable PE from a validated CLR LIR method. The emitter is
/// intentionally limited to the call sites needed by the P0 feasibility spike;
/// broader method and type metadata belongs to the later backend milestones.
/// </summary>
public static class ClrLirAssemblyEmitter
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
    public static GeneratedAssembly Emit(ClrLirMethod method, string assemblyName)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ClrLirValidationResult validation = method.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Cannot emit invalid CLR LIR: {string.Join("; ", validation.Diagnostics)}");
        }

        if (assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(assemblyName, Path.GetFileName(assemblyName), StringComparison.Ordinal))
        {
            throw new ArgumentException("The assembly name must be a simple file name.", nameof(assemblyName));
        }

        var metadata = new MetadataBuilder();
        var ilStream = new BlobBuilder();
        var methodBodyStream = new MethodBodyStreamEncoder(ilStream);
        Guid moduleVersionId = CreateModuleVersionId(assemblyName, method);

        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString(assemblyName + ".dll"),
            mvid: metadata.GetOrAddGuid(moduleVersionId),
            encId: default,
            encBaseId: default);
        metadata.AddAssembly(
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
        TypeReferenceHandle objectType = metadata.AddTypeReference(
            resolutionScope: systemRuntime,
            @namespace: metadata.GetOrAddString("System"),
            name: metadata.GetOrAddString("Object"));
        TypeReferenceHandle consoleType = metadata.AddTypeReference(
            resolutionScope: systemConsole,
            @namespace: metadata.GetOrAddString("System"),
            name: metadata.GetOrAddString("Console"));

        var methodCode = new BlobBuilder();
        var controlFlow = new ControlFlowBuilder();
        var instructionEncoder = new InstructionEncoder(methodCode, controlFlow);
        int maxStack = ClrLirEmitter.EncodeInstructions(
            method,
            metadata,
            site => AddCallReference(metadata, consoleType, site),
            instructionEncoder);
        StandaloneSignatureHandle localSignature = AddLocalSignature(metadata, method.Locals);
        int methodBodyOffset = methodBodyStream.AddMethodBody(
            instructionEncoder,
            maxStack,
            localSignature);

        MethodDefinitionHandle entryPoint = metadata.AddMethodDefinition(
            attributes: MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            implAttributes: MethodImplAttributes.IL | MethodImplAttributes.Managed,
            name: metadata.GetOrAddString(method.Name),
            signature: CreateMethodSignature(metadata, method.ReturnType, method.Parameters),
            bodyOffset: methodBodyOffset,
            parameterList: MetadataTokens.ParameterHandle(1));

        FieldDefinitionHandle firstField = MetadataTokens.FieldDefinitionHandle(1);
        metadata.AddTypeDefinition(
            attributes: TypeAttributes.NotPublic,
            @namespace: default,
            name: metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: firstField,
            methodList: entryPoint);
        metadata.AddTypeDefinition(
            attributes: TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            @namespace: metadata.GetOrAddString("RustSharp.Generated"),
            name: metadata.GetOrAddString("Program"),
            baseType: objectType,
            fieldList: firstField,
            methodList: entryPoint);

        var peHeader = new PEHeaderBuilder(
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware,
            subsystem: Subsystem.WindowsCui);
        var peBuilder = new ManagedPEBuilder(
            peHeader,
            new MetadataRootBuilder(metadata),
            ilStream,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: ComputeContentId);
        var peImage = new BlobBuilder();
        peBuilder.Serialize(peImage);

        return new GeneratedAssembly(peImage.ToArray(), null, RuntimeConfig);
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
            site.ParameterTypes[0] == ClrLirType.Void)
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
        IReadOnlyList<ClrLirType> parameterTypes)
    {
        var signature = new BlobBuilder();
        new BlobEncoder(signature)
            .MethodSignature(isInstanceMethod: false)
            .Parameters(
                parameterCount: parameterTypes.Count,
                returnTypeEncoder => EncodeReturnType(returnTypeEncoder, methodReturnType),
                parameters =>
                {
                    foreach (ClrLirType parameterType in parameterTypes)
                    {
                        EncodeParameterType(parameters.AddParameter().Type(isByRef: false), parameterType);
                    }
                });

        return metadata.GetOrAddBlob(signature);
    }

    private static void EncodeReturnType(
        System.Reflection.Metadata.Ecma335.ReturnTypeEncoder encoder,
        ClrLirType type)
    {
        if (type == ClrLirType.Void)
        {
            encoder.Void();
            return;
        }

        EncodeSignatureType(encoder.Type(isByRef: false), type);
    }

    private static void EncodeParameterType(
        System.Reflection.Metadata.Ecma335.SignatureTypeEncoder encoder,
        ClrLirType type)
    {
        if (type == ClrLirType.Void)
        {
            throw new ArgumentException("Void is not a valid parameter type.", nameof(type));
        }

        EncodeSignatureType(encoder, type);
    }

    private static void EncodeSignatureType(
        System.Reflection.Metadata.Ecma335.SignatureTypeEncoder encoder,
        ClrLirType type)
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
            default:
                throw new ArgumentException($"Unsupported CLR LIR signature type '{type}'.", nameof(type));
        }
    }

    private static StandaloneSignatureHandle AddLocalSignature(
        MetadataBuilder metadata,
        ImmutableArray<ClrLirLocal> locals)
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

            EncodeSignatureType(variables.AddVariable().Type(isByRef: false, isPinned: false), local.Type);
        }

        return metadata.AddStandaloneSignature(metadata.GetOrAddBlob(signature));
    }

    private static Guid CreateModuleVersionId(string assemblyName, ClrLirMethod method)
    {
        var descriptor = new StringBuilder(EmitterIdentity)
            .Append('\0')
            .Append(assemblyName)
            .Append('\0')
            .Append(method.Name)
            .Append('\0')
            .Append(method.ReturnType.Kind);
        _ = descriptor.Append('\0').Append("params");
        foreach (ClrLirType parameter in method.Parameters)
        {
            _ = descriptor.Append('\0').Append(parameter.Kind);
        }

        _ = descriptor.Append('\0').Append("locals");
        foreach (ClrLirLocal local in method.Locals)
        {
            _ = descriptor.Append('\0').Append(local.Name).Append(':').Append(local.Type.Kind);
        }

        foreach (ClrLirBlock block in method.Blocks)
        {
            _ = descriptor.Append('\0').Append(block.Label);
            foreach (ClrLirInstruction instruction in block.Instructions)
            {
                _ = descriptor.Append('\0').Append(instruction.GetType().Name);
                switch (instruction)
                {
                    case ClrLirLoadInt32 loadInt32:
                        _ = descriptor.Append(':').Append(loadInt32.Value);
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
                    case ClrLirStoreLocal storeLocal:
                        _ = descriptor.Append(':').Append(storeLocal.Index);
                        break;
                    case ClrLirCall call:
                        _ = descriptor.Append(':').Append(call.Site.Name).Append(':').Append(call.Site.ReturnType.Kind);
                        foreach (ClrLirType parameterType in call.Site.ParameterTypes)
                        {
                            _ = descriptor.Append(':').Append(parameterType.Kind);
                        }

                        break;
                    case ClrLirBranch branch:
                        _ = descriptor.Append(':').Append(branch.Target);
                        break;
                    case ClrLirBranchTrue branchTrue:
                        _ = descriptor.Append(':').Append(branchTrue.Target);
                        break;
                }
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
