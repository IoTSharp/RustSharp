using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private int RegisterExternalFunction(SafeCoreExternalFunction external, SafeCoreType signature,
            SafeCoreHirNode anchor, bool isDestructor)
        {
            Step(anchor, 0);
            if (_externalFunctionIds.TryGetValue(external, out int existing))
            {
                SafeCoreMirExternalFunction previous = _externalFunctions[existing - _functionNodes.Count];
                if (previous.Signature != signature || previous.IsDestructor != isDestructor) Invalid(anchor);
                return existing;
            }
            if (_functionNodes.Count + _externalFunctions.Count >= options.MaximumFunctions) Limit(anchor);
            int id = _functionNodes.Count + _externalFunctions.Count;
            _externalFunctions.Add(new(id, external, signature, Source(anchor)) { IsDestructor = isDestructor });
            _externalFunctionIds.Add(external, id);
            return id;
        }

        private void RegisterImportedDestructors()
        {
            foreach (SafeCoreMirAdtLayout layout in _adtLayouts.Values)
            {
                SafeCoreHirNode anchor = input.Hir.GetNode(layout.Source.HirNodeId);
                Step(anchor, 0);
                if (layout.ExternalDropFunction is not { } descriptor) continue;
                if (layout.IsCopy || layout.ExternalAssemblyName is null || descriptor.AssemblyName != layout.ExternalAssemblyName ||
                    descriptor.SourceSchema != "rustsharp-source-call-v1" || descriptor.SourceParameterTypes.IsDefault ||
                    descriptor.SourceParameterTypes.Length != 1 || descriptor.SourceReturnType != "()" ||
                    descriptor.CallPanicStrategy is not ("unwind" or "abort") ||
                    descriptor.CallParameterContracts.IsDefault || descriptor.CallParameterContracts.Length != 1 ||
                    descriptor.CallParameterContracts[0] != "borrow:mut" || descriptor.CallReturnContract != "unit" ||
                    descriptor.ReturnOrigins.IsDefault || descriptor.ReturnOrigins.Length != 0) Invalid(anchor);
                SafeCoreType declared;
                try { declared = SafeCoreSourceTypeCodec.Parse(descriptor.SourceParameterTypes[0], cancellation); }
                catch (ArgumentException) { Invalid(anchor); throw; }
                if (declared.Kind != K.Reference || !declared.IsMutable || declared.ElementType!.Kind != K.Adt ||
                    descriptor.NominalScope is null ||
                    descriptor.NominalScope + "::" + (declared.ElementType.Name!.StartsWith("crate::", StringComparison.Ordinal)
                        ? declared.ElementType.Name[7..] : declared.ElementType.Name) != layout.Type.Name) Invalid(anchor);
                SafeCoreType signature = SafeCoreType.Function([SafeCoreType.Reference(layout.Type, true)],
                    SafeCoreType.Primitive(K.Unit), descriptor.SourceQualifiedName);
                int id = RegisterExternalFunction(descriptor, signature, anchor, isDestructor: true);
                if (!_dropFunctions.TryAdd(layout.Type.Name!, id)) Invalid(anchor);
            }
        }
    }
}
