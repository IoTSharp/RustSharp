using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace RustSharp.CodeGen.IL;

public static partial class RustSharpMetadataMethodBodies
{
    private const int MaximumReferenceRows = 65_536;
    private const int MaximumReferenceBytes = 64 * 1024 * 1024;

    private static byte[] CaptureReferenceMetadata(PEReader pe, MetadataReader metadata, Stopwatch clock,
        ref long totalBytes, CancellationToken cancellationToken)
    {
        // Bind token meaning, rather than heap offsets. Custom attributes and
        // Module.Mvid are deliberately absent: metadata JSON is stamped between
        // the two emissions, and neither value determines a referenced call.
        TableIndex[] tables = [TableIndex.MemberRef, TableIndex.TypeRef, TableIndex.AssemblyRef,
            TableIndex.TypeSpec, TableIndex.MethodSpec, TableIndex.TypeDef, TableIndex.MethodDef,
            TableIndex.Field, TableIndex.Param, TableIndex.Constant, TableIndex.StandAloneSig, TableIndex.ModuleRef];
        int rowTotal = 0;
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
        Check();
        if (metadata.GetHeapSize(HeapIndex.String) > MaximumReferenceBytes)
            throw new InvalidDataException("Rust# actual metadata string heap exceeds its execution evidence byte budget.");
        foreach (TableIndex table in tables)
        {
            Check();
            int rows = metadata.GetTableRowCount(table);
            if (rows > MaximumReferenceRows ||
                (rowTotal += rows) > MaximumReferenceRows)
                throw new InvalidDataException("Rust# referenced metadata exceeds its table row evidence budget.");
            writer.Write((int)table);
            writer.Write(rows);
            for (int row = 1; row <= rows; row++)
            {
                Check();
                switch (table)
                {
                    case TableIndex.MemberRef:
                        MemberReference member = metadata.GetMemberReference(MetadataTokens.MemberReferenceHandle(row));
                        writer.Write(MetadataTokens.GetToken(member.Parent));
                        WriteName(member.Name);
                        WriteBlob(member.Signature);
                        break;
                    case TableIndex.TypeRef:
                        TypeReference type = metadata.GetTypeReference(MetadataTokens.TypeReferenceHandle(row));
                        writer.Write(MetadataTokens.GetToken(type.ResolutionScope));
                        WriteName(type.Namespace);
                        WriteName(type.Name);
                        break;
                    case TableIndex.AssemblyRef:
                        AssemblyReference assembly = metadata.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(row));
                        WriteName(assembly.Name);
                        WriteName(assembly.Culture);
                        writer.Write(assembly.Version.Major);
                        writer.Write(assembly.Version.Minor);
                        writer.Write(assembly.Version.Build);
                        writer.Write(assembly.Version.Revision);
                        writer.Write((int)assembly.Flags);
                        WriteBlob(assembly.PublicKeyOrToken);
                        WriteBlob(assembly.HashValue);
                        break;
                    case TableIndex.TypeSpec:
                        WriteBlob(metadata.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(row)).Signature);
                        break;
                    case TableIndex.MethodSpec:
                        MethodSpecification method = metadata.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));
                        writer.Write(MetadataTokens.GetToken(method.Method));
                        WriteBlob(method.Signature);
                        break;
                    case TableIndex.TypeDef:
                        TypeDefinition declaredType = metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row));
                        WriteName(declaredType.Namespace);
                        WriteName(declaredType.Name);
                        writer.Write((int)declaredType.Attributes);
                        writer.Write(MetadataTokens.GetToken(declaredType.BaseType));
                        writer.Write(MetadataTokens.GetToken(declaredType.GetDeclaringType()));
                        TypeLayout layout = declaredType.GetLayout();
                        writer.Write(layout.PackingSize);
                        writer.Write(layout.Size);
                        break;
                    case TableIndex.MethodDef:
                        MethodDefinition declaredMethod = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
                        writer.Write(MetadataTokens.GetToken(declaredMethod.GetDeclaringType()));
                        writer.Write((int)declaredMethod.Attributes);
                        writer.Write((int)declaredMethod.ImplAttributes);
                        WriteName(declaredMethod.Name);
                        WriteBlob(declaredMethod.Signature);
                        ParameterHandleCollection parameters = declaredMethod.GetParameters();
                        if (parameters.Count > MaximumReferenceRows)
                            throw new InvalidDataException("Rust# actual method parameters exceed their metadata row evidence budget.");
                        writer.Write(parameters.Count);
                        foreach (ParameterHandle parameter in parameters)
                        {
                            Check();
                            writer.Write(MetadataTokens.GetToken(parameter));
                        }
                        break;
                    case TableIndex.Field:
                        FieldDefinition field = metadata.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(row));
                        writer.Write(MetadataTokens.GetToken(field.GetDeclaringType()));
                        writer.Write((int)field.Attributes);
                        writer.Write(field.GetOffset());
                        writer.Write(MetadataTokens.GetToken(field.GetDefaultValue()));
                        WriteName(field.Name);
                        WriteBlob(field.Signature);
                        break;
                    case TableIndex.Param:
                        Parameter declaredParameter = metadata.GetParameter(MetadataTokens.ParameterHandle(row));
                        writer.Write((int)declaredParameter.Attributes);
                        writer.Write(declaredParameter.SequenceNumber);
                        writer.Write(MetadataTokens.GetToken(declaredParameter.GetDefaultValue()));
                        WriteName(declaredParameter.Name);
                        break;
                    case TableIndex.Constant:
                        Constant constant = metadata.GetConstant(MetadataTokens.ConstantHandle(row));
                        writer.Write(MetadataTokens.GetToken(constant.Parent));
                        writer.Write((int)constant.TypeCode);
                        WriteBlob(constant.Value);
                        break;
                    case TableIndex.StandAloneSig:
                        WriteBlob(metadata.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row)).Signature);
                        break;
                    case TableIndex.ModuleRef:
                        WriteName(metadata.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name);
                        break;
                }
            }
        }
        // ldstr tokens address #US rather than a table row. This heap contains
        // actual runtime literals and is independent of the JSON attribute blob.
        int userStringBytes = metadata.GetHeapSize(HeapIndex.UserString);
        Check();
        if (userStringBytes > MaximumReferenceBytes - payload.Length - sizeof(int))
            throw new InvalidDataException("Rust# actual user strings exceed their execution metadata byte budget.");
        writer.Write(userStringBytes);
        if (userStringBytes > 0)
            writer.Write(pe.GetMetadata().GetContent().AsSpan(metadata.GetHeapMetadataOffset(HeapIndex.UserString), userStringBytes));
        Check();
        totalBytes += payload.Length;
        if (totalBytes > RustSharpMetadataConsumer.MaximumAssemblyBytes)
            throw new InvalidDataException("Rust# body and reference metadata exceed their cumulative evidence size budget.");
        return SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));

        void WriteName(StringHandle handle)
        {
            Check();
            string value = metadata.GetString(handle);
            if (Encoding.UTF8.GetByteCount(value) > MaximumReferenceBytes - payload.Length - 8)
                throw new InvalidDataException("Rust# referenced metadata names exceed their evidence byte budget.");
            writer.Write(value);
        }

        void WriteBlob(BlobHandle handle)
        {
            Check();
            BlobReader blob = metadata.GetBlobReader(handle);
            if (blob.Length > MaximumReferenceBytes - payload.Length - sizeof(int))
                throw new InvalidDataException("Rust# referenced metadata signatures exceed their evidence byte budget.");
            writer.Write(blob.Length);
            writer.Write(blob.ReadBytes(blob.Length));
        }

        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5) || payload.Length > MaximumReferenceBytes)
                throw new InvalidDataException("Rust# referenced metadata fingerprinting exceeded its time or byte budget.");
        }
    }
}
