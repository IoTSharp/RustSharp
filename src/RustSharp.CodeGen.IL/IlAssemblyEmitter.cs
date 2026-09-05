using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.CodeGen.IL;

public sealed class IlAssemblyEmitter
{
    private const ushort PortablePdbVersion = 0x0100;
    private const string EmitterIdentity = "RustSharp.CodeGen.IL/0.1.0";

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

    private static readonly Guid Sha256DocumentHashAlgorithm =
        new("8829d00f-11b8-4213-878b-770e8597ac16");

    private const int MaximumPortablePdbLine = 0x20000000;
    private const int MaximumPortablePdbColumn = 0x10000;

    public static GeneratedAssembly Emit(
        CompilationUnitSyntax syntax,
        string sourceText,
        string sourcePath,
        string assemblyName,
        string? pdbFileName = null,
        ReadOnlyMemory<byte> sourceBytes = default)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        var resolvedPdbFileName = string.IsNullOrWhiteSpace(pdbFileName)
            ? assemblyName + ".pdb"
            : Path.GetFileName(pdbFileName);
        if (string.IsNullOrWhiteSpace(resolvedPdbFileName))
        {
            throw new ArgumentException("The PDB file name must be a simple file name.", nameof(pdbFileName));
        }

        if (!string.IsNullOrWhiteSpace(pdbFileName) &&
            !string.Equals(resolvedPdbFileName, pdbFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The PDB file name must be a simple file name.", nameof(pdbFileName));
        }

        if (syntax.Statements is null)
        {
            throw new ArgumentException("The compilation unit must contain a statement collection.", nameof(syntax));
        }

        var metadata = new MetadataBuilder();
        var ilStream = new BlobBuilder();
        var methodBodyStream = new MethodBodyStreamEncoder(ilStream);
        ReadOnlyMemory<byte> effectiveSourceBytes = sourceBytes.IsEmpty
            ? Encoding.UTF8.GetBytes(sourceText)
            : sourceBytes;

        string moduleName = assemblyName + ".dll";
        Guid moduleVersionId = CreateModuleVersionId(assemblyName, effectiveSourceBytes.Span);

        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString(moduleName),
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
        AssemblyReferenceHandle systemRuntime = AddFrameworkReference(metadata, "System.Runtime", runtimePublicKeyToken);
        AssemblyReferenceHandle systemConsole = AddFrameworkReference(metadata, "System.Console", runtimePublicKeyToken);

        TypeReferenceHandle objectType = metadata.AddTypeReference(
            resolutionScope: systemRuntime,
            @namespace: metadata.GetOrAddString("System"),
            name: metadata.GetOrAddString("Object"));

        TypeReferenceHandle consoleType = metadata.AddTypeReference(
            resolutionScope: systemConsole,
            @namespace: metadata.GetOrAddString("System"),
            name: metadata.GetOrAddString("Console"));

        MemberReferenceHandle writeLine = metadata.AddMemberReference(
            parent: consoleType,
            name: metadata.GetOrAddString("WriteLine"),
            signature: CreateWriteLineSignature(metadata));

        var methodCode = new BlobBuilder();
        var instructionEncoder = new InstructionEncoder(methodCode);
        var sequencePoints = new List<SequencePointData>(syntax.Statements.Count);

        foreach (PrintStatementSyntax statement in syntax.Statements)
        {
            var statementOffset = instructionEncoder.Offset;
            instructionEncoder.LoadString(metadata.GetOrAddUserString(statement.Value));
            instructionEncoder.Call(writeLine);
            sequencePoints.Add(new SequencePointData(statementOffset, statement.Span));
        }

        instructionEncoder.OpCode(ILOpCode.Ret);
        int methodBodyOffset = methodBodyStream.AddMethodBody(
            instructionEncoder,
            maxStack: syntax.Statements.Count == 0 ? 0 : 1);

        MethodDefinitionHandle mainMethod = metadata.AddMethodDefinition(
            attributes: MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            implAttributes: MethodImplAttributes.IL | MethodImplAttributes.Managed,
            name: metadata.GetOrAddString("Main"),
            signature: CreateMainSignature(metadata),
            bodyOffset: methodBodyOffset,
            parameterList: MetadataTokens.ParameterHandle(1));

        FieldDefinitionHandle firstField = MetadataTokens.FieldDefinitionHandle(1);
        metadata.AddTypeDefinition(
            attributes: TypeAttributes.NotPublic,
            @namespace: default,
            name: metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: firstField,
            methodList: mainMethod);

        metadata.AddTypeDefinition(
            attributes: TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            @namespace: metadata.GetOrAddString("RustSharp.Generated"),
            name: metadata.GetOrAddString("Program"),
            baseType: objectType,
            fieldList: firstField,
            methodList: mainMethod);

        (BlobBuilder pdbImage, BlobContentId pdbId) = BuildPortablePdb(
            metadata,
            mainMethod,
            sourceText,
            sourcePath,
            effectiveSourceBytes,
            sequencePoints);

        var debugDirectory = new DebugDirectoryBuilder();
        debugDirectory.AddCodeViewEntry(
            resolvedPdbFileName,
            pdbId,
            PortablePdbVersion);

        var peHeader = new PEHeaderBuilder(
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware,
            subsystem: Subsystem.WindowsCui);

        var peBuilder = new ManagedPEBuilder(
            peHeader,
            new MetadataRootBuilder(metadata),
            ilStream,
            debugDirectoryBuilder: debugDirectory,
            entryPoint: mainMethod,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: ComputeContentId);

        var peImage = new BlobBuilder();
        peBuilder.Serialize(peImage);

        return new GeneratedAssembly(
            peImage.ToArray(),
            pdbImage.ToArray(),
            RuntimeConfig);
    }

    private static AssemblyReferenceHandle AddFrameworkReference(
        MetadataBuilder metadata,
        string name,
        BlobHandle publicKeyToken)
    {
        return metadata.AddAssemblyReference(
            name: metadata.GetOrAddString(name),
            version: RuntimeAssemblyVersion,
            culture: default,
            publicKeyOrToken: publicKeyToken,
            flags: default,
            hashValue: default);
    }

    private static BlobHandle CreateMainSignature(MetadataBuilder metadata)
    {
        var signature = new BlobBuilder();
        new BlobEncoder(signature)
            .MethodSignature(isInstanceMethod: false)
            .Parameters(
                parameterCount: 0,
                returnType => returnType.Void(),
                parameters => { });

        return metadata.GetOrAddBlob(signature);
    }

    private static BlobHandle CreateWriteLineSignature(MetadataBuilder metadata)
    {
        var signature = new BlobBuilder();
        new BlobEncoder(signature)
            .MethodSignature(isInstanceMethod: false)
            .Parameters(
                parameterCount: 1,
                returnType => returnType.Void(),
                parameters => parameters.AddParameter().Type().String());

        return metadata.GetOrAddBlob(signature);
    }

    private static (BlobBuilder Image, BlobContentId Id) BuildPortablePdb(
        MetadataBuilder typeSystemMetadata,
        MethodDefinitionHandle entryPoint,
        string sourceText,
        string sourcePath,
        ReadOnlyMemory<byte> sourceBytes,
        IReadOnlyList<SequencePointData> sequencePoints)
    {
        var pdbMetadata = new MetadataBuilder();
        byte[] sourceHash = SHA256.HashData(sourceBytes.Span);

        DocumentHandle document = pdbMetadata.AddDocument(
            name: pdbMetadata.GetOrAddDocumentName(sourcePath),
            hashAlgorithm: pdbMetadata.GetOrAddGuid(Sha256DocumentHashAlgorithm),
            hash: pdbMetadata.GetOrAddBlob(sourceHash),
            language: default);

        BlobHandle sequencePointsBlob = CreateSequencePointsBlob(
            pdbMetadata,
            sourceText,
            sequencePoints);
        pdbMetadata.AddMethodDebugInformation(
            sequencePointsBlob.IsNil ? default : document,
            sequencePointsBlob);

        var pdbImage = new BlobBuilder();
        var pdbBuilder = new PortablePdbBuilder(
            pdbMetadata,
            typeSystemMetadata.GetRowCounts(),
            entryPoint,
            ComputeContentId);

        BlobContentId pdbId = pdbBuilder.Serialize(pdbImage);
        return (pdbImage, pdbId);
    }

    internal static BlobHandle CreateSequencePointsBlob(
        MetadataBuilder pdbMetadata,
        string sourceText,
        IReadOnlyList<SequencePointData> sequencePoints,
        int localSignatureRow = 0)
    {
        if (sequencePoints.Count == 0)
        {
            return default;
        }

        var lineStarts = BuildLineStarts(sourceText);
        var blob = new BlobBuilder();

        blob.WriteCompressedInteger(localSignatureRow);

        var previousIlOffset = 0;
        var previousStartLine = 0;
        var previousStartColumn = 0;
        var hasPreviousNonHidden = false;

        foreach (SequencePointData sequencePoint in sequencePoints)
        {
            SourceSpanLocation location = GetSpanLocation(
                sourceText,
                lineStarts,
                sequencePoint.Span);
            if (!IsPortablePdbLocation(location))
            {
                // A very long source line cannot be represented by the v1.0 PDB
                // column field. It is better to omit that mapping than emit an
                // invalid PDB that a debugger cannot load.
                continue;
            }

            int ilOffsetDelta = sequencePoint.IlOffset - previousIlOffset;
            if (ilOffsetDelta < 0 || (hasPreviousNonHidden && ilOffsetDelta == 0))
            {
                continue;
            }

            int deltaLines = location.EndLine - location.StartLine;
            int deltaColumns = location.EndColumn - location.StartColumn;
            if (deltaLines == 0 && deltaColumns <= 0)
            {
                continue;
            }

            blob.WriteCompressedInteger(ilOffsetDelta);
            blob.WriteCompressedInteger(deltaLines);
            if (deltaLines == 0)
            {
                blob.WriteCompressedInteger(deltaColumns);
            }
            else
            {
                blob.WriteCompressedSignedInteger(deltaColumns);
            }

            if (!hasPreviousNonHidden)
            {
                blob.WriteCompressedInteger(location.StartLine);
                blob.WriteCompressedInteger(location.StartColumn);
                hasPreviousNonHidden = true;
            }
            else
            {
                blob.WriteCompressedSignedInteger(location.StartLine - previousStartLine);
                blob.WriteCompressedSignedInteger(location.StartColumn - previousStartColumn);
            }

            previousIlOffset = sequencePoint.IlOffset;
            previousStartLine = location.StartLine;
            previousStartColumn = location.StartColumn;
        }

        return hasPreviousNonHidden
            ? pdbMetadata.GetOrAddBlob(blob)
            : default;
    }

    private static int[] BuildLineStarts(string sourceText)
    {
        var starts = new List<int>(capacity: sourceText.Length < 1024 ? sourceText.Length + 1 : 1024) { 0 };
        for (var index = 0; index < sourceText.Length; index++)
        {
            if (sourceText[index] == '\r')
            {
                if (index + 1 < sourceText.Length && sourceText[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (sourceText[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return starts.ToArray();
    }

    private static SourceSpanLocation GetSpanLocation(
        string sourceText,
        IReadOnlyList<int> lineStarts,
        TextSpan span)
    {
        int start = Math.Clamp(span.Start, 0, sourceText.Length);
        int end = Math.Clamp(span.End, start, sourceText.Length);
        SourcePoint startPoint = GetSourcePoint(lineStarts, start);
        SourcePoint endPoint = GetSourcePoint(lineStarts, end);
        return new SourceSpanLocation(
            startPoint.Line,
            startPoint.Column,
            endPoint.Line,
            endPoint.Column);
    }

    private static SourcePoint GetSourcePoint(IReadOnlyList<int> lineStarts, int offset)
    {
        var low = 0;
        var high = lineStarts.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (lineStarts[middle] <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        int lineIndex = Math.Max(0, high);
        return new SourcePoint(
            lineIndex + 1,
            offset - lineStarts[lineIndex]);
    }

    private static bool IsPortablePdbLocation(SourceSpanLocation location) =>
        location.StartLine is >= 1 and < MaximumPortablePdbLine &&
        location.EndLine is >= 1 and < MaximumPortablePdbLine &&
        location.StartColumn is >= 0 and < MaximumPortablePdbColumn &&
        location.EndColumn is >= 0 and < MaximumPortablePdbColumn &&
        (location.EndLine > location.StartLine || location.EndColumn > location.StartColumn);

    private static Guid CreateModuleVersionId(
        string assemblyName,
        ReadOnlySpan<byte> sourceBytes)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(
            string.Concat(EmitterIdentity, "\0", assemblyName, "\0", sourceBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "\0"));
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        incrementalHash.AppendData(prefix);
        incrementalHash.AppendData(sourceBytes);
        incrementalHash.GetHashAndReset(hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..guidBytes.Length].CopyTo(guidBytes);
        return new Guid(guidBytes);
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

    internal readonly record struct SequencePointData(int IlOffset, TextSpan Span);

    private readonly record struct SourcePoint(int Line, int Column);

    private readonly record struct SourceSpanLocation(
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn);
}
