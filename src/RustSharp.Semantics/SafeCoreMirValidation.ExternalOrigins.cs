namespace RustSharp.Semantics;
public static partial class SafeCoreMirValidation
{
    private sealed partial class Validator
    {
        private void ValidateExternalOrigins(SafeCoreMirExternalFunction external)
        {
            SafeCoreType result = external.Signature.ReturnType;
            var descriptor = external.ExternalFunction;
            if (descriptor.ReturnOrigins.Length > SafeCoreSourceOriginCodec.MaximumItems)
                throw new SafeCoreMirLimitException("Imported return origin count exceeds the arena bound.");
            bool containsReferences = ContainsExternalReference(result, 0);
            if ((containsReferences || descriptor.ReturnOrigins.Length != 0) &&
                descriptor.SourceSchema != "rustsharp-source-call-v1")
            {
                Error(SafeCoreMirDiagnosticCodes.InvalidInput,
                    "Imported reference slots require producer-declared source types and return origins.", external.Source);
                return;
            }
            IReadOnlyList<string> errors = SafeCoreSourceOriginCodec.ValidateContract(
                external.Signature.ParameterTypes, result, descriptor.ReturnOrigins, Fields,
                descriptor.SourceParameterStaticLifetimes, StaticFields, ReturnedFields, options.CancellationToken);
            foreach (string error in errors)
            {
                Step();
                Error(SafeCoreMirDiagnosticCodes.InvalidInput, error, external.Source);
            }

            IReadOnlyList<(string Name, SafeCoreType Type)> Fields(SafeCoreType type)
            {
                Step();
                if (type.Name is null || !_adtLayouts.TryGetValue(type.Name, out SafeCoreMirAdtLayout? layout))
                    throw new ArgumentException("Imported nominal origins require a checked declaration layout.");
                return layout.Fields.Select(static field => (field.Name, field.Type)).ToArray();
            }

            IReadOnlyList<(string Name, bool RequiresStaticLifetime)> StaticFields(SafeCoreType type)
            {
                Step();
                if (type.Name is null || !_adtLayouts.TryGetValue(type.Name, out SafeCoreMirAdtLayout? layout))
                    throw new ArgumentException("Imported static field origins require a checked declaration layout.");
                return layout.Fields.Select(static field => (field.Name, field.RequiresStaticLifetime)).ToArray();
            }

            IReadOnlyList<(string Name, SafeCoreType Type)>? ReturnedFields(SafeCoreType type, IReadOnlyList<SafeCoreMirProjection> path)
            {
                Step();
                if (type.Name is null || !_adtLayouts.TryGetValue(type.Name, out SafeCoreMirAdtLayout? layout))
                    throw new ArgumentException("Imported enum origins require a checked declaration layout.");
                if (layout.Variants.Count == 0) return null;
                string[] encodedPath = path.Select(projection => SafeCoreSourceOriginCodec.FormatProjection(projection, options.CancellationToken)).ToArray();
                var selected = new HashSet<int>();
                bool found = false;
                foreach (var evidence in descriptor.SourceReturnVariants)
                {
                    Step();
                    if (!evidence.ValuePath.SequenceEqual(encodedPath)) continue;
                    found = true;
                    var declaredLayouts = descriptor.SourceValueTypes.Where(value =>
                        value.ClrName == layout.ExternalClrName && value.AssemblyName == layout.ExternalAssemblyName).Take(2).ToArray();
                    if (declaredLayouts.Length != 1)
                        throw new ArgumentException("Imported enum return origins require one exact original producer layout identity.");
                    var declared = declaredLayouts[0].Variants.FirstOrDefault(value => value.Name == evidence.VariantName);
                    if (declared is null)
                        throw new ArgumentException("Imported enum return origin variant does not belong to its declared producer enum.");
                    string canonicalName = layout.Type.Name + "::" + declared.SourceName;
                    SafeCoreMirAdtVariant? variant = layout.Variants.FirstOrDefault(value => value.Name == canonicalName);
                    if (variant is null) throw new ArgumentException("Imported enum return origins select an undeclared active variant.");
                    for (int index = 0; index < variant.Fields.Count; index++)
                    {
                        Step();
                        selected.Add(variant.FieldOffset + index);
                    }
                }
                if (!found) throw new ArgumentException("Imported enum return origins are missing active variant evidence.");
                return layout.Fields.Where((_, index) => selected.Contains(index)).Select(static field => (field.Name, field.Type)).ToArray();
            }
        }

        private bool ContainsExternalReference(SafeCoreType type, int depth)
        {
            Step();
            if (depth > options.MaximumTypeDepth) throw new SafeCoreMirLimitException("Imported reference nesting exceeds its bound.");
            if (type.Kind == SafeCoreSemanticTypeKind.Reference) return true;
            if (type.Kind is SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array)
                foreach (SafeCoreType element in type.Elements)
                    if (ContainsExternalReference(element, depth + 1)) return true;
            if (type.Kind == SafeCoreSemanticTypeKind.Adt && type.Name is { } name &&
                _adtLayouts.TryGetValue(name, out SafeCoreMirAdtLayout? layout))
                foreach (SafeCoreMirAdtField field in layout.Fields)
                    if (ContainsExternalReference(field.Type, depth + 1)) return true;
            return false;
        }

    }
}
