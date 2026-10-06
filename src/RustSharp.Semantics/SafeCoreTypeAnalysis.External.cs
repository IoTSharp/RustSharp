using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreTypeAnalysis
{
    private sealed partial class Checker
    {
        private readonly Dictionary<string, SafeCoreMirAdtLayout> _importedLayouts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SafeCoreExternalTypeKind> _importedKinds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SafeCoreExternalValueType> _importedEvidence = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Scope, string Name), string> _importedNominalNames = [];
        private readonly Dictionary<SafeCoreType, SafeCoreMirImportedStructuralType> _importedStructuralTypes = [];

        private void InitializeExternalTypes()
        {
            SafeCoreHirNode anchor = _hir.Root!;
            foreach (SafeCoreCrate crate in _hir.NameResolution!.Crates)
            {
                Step(anchor, 0);
                foreach (SafeCoreExternalType exported in crate.TypeExports.IsDefault ? [] : crate.TypeExports)
                    RegisterNominalName(exported.Layout, exported.NominalScope);
                foreach (SafeCoreExternalFunction function in crate.Exports.IsDefault ? [] : crate.Exports)
                {
                    Step(anchor, 0);
                    if (function.SourceSchema != "rustsharp-source-call-v1") continue;
                    foreach (SafeCoreExternalValueType layout in function.SourceValueTypes)
                        RegisterNominalName(layout, function.NominalScope ?? "");
                }
            }
            foreach (SafeCoreCrate crate in _hir.NameResolution!.Crates)
            {
                Step(anchor, 0);
                foreach (SafeCoreExternalType exported in crate.TypeExports.IsDefault ? [] : crate.TypeExports)
                    RegisterExternalLayout(exported.Layout, exported.NominalScope, anchor, exported.Kind, exported.IsPublic);
                foreach (SafeCoreExternalFunction function in crate.Exports.IsDefault ? [] : crate.Exports)
                {
                    Step(anchor, 0);
                    if (function.SourceSchema != "rustsharp-source-call-v1") continue;
                    foreach (SafeCoreExternalValueType layout in function.SourceValueTypes)
                        RegisterExternalLayout(layout, function.NominalScope ?? "", anchor);
                    foreach (SafeCoreExternalStructuralType structural in function.StructuralTypes)
                        RegisterExternalStructural(structural, anchor);
                }
                foreach (SafeCoreExternalStructuralType structural in crate.StructuralTypes.IsDefault ? [] : crate.StructuralTypes)
                    RegisterExternalStructural(structural, anchor);
            }
            foreach (SafeCoreSymbol symbol in _hir.NameResolution.Symbols)
            {
                Step(anchor, 0);
                string key = Key(symbol);
                if (symbol.ExternalType is { } external)
                {
                    string identity = ResolveImportedName(external.Layout.Name, external.NominalScope, anchor);
                    _namedTypes[key] = _importedLayouts[identity].Type;
                    if (external.Kind != SafeCoreExternalTypeKind.Enum) BindConstructor(key, _adts[identity][0]);
                }
                if (symbol.ExternalEnumVariant is { } externalVariant)
                {
                    string identity = ResolveImportedName(externalVariant.EnumType.Layout.Name, externalVariant.EnumType.NominalScope, anchor);
                    AdtShape? shape = _adts[identity].FirstOrDefault(shape => shape.Node.Name == externalVariant.Variant.SourceName);
                    if (shape is null) Fail(anchor, "RST2002", "The exported enum variant has no checked producer layout.");
                    BindConstructor(key, shape!);
                }
            }

            void BindConstructor(string key, AdtShape shape)
            {
                _constructors[key] = shape;
                if (shape.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct))
                    _values[key] = SafeCoreType.Function(shape.Fields.Select(static field => field.Type).ToArray(), shape.Type, key);
                else if (shape.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct)) _values[key] = shape.Type;
            }

            void RegisterNominalName(SafeCoreExternalValueType layout, string sourceScope)
            {
                Step(anchor, 0);
                if (layout.Owner is { } owner)
                {
                    if (owner.AssemblyName != layout.AssemblyName || owner.ClrName != layout.ClrName ||
                        layout.NominalScope is null || layout.NominalSourceName != owner.SourceName ||
                        owner.ModuleVersionId == Guid.Empty || !Hash(owner.SourceSha256) || !Hash(owner.AssemblySha256))
                        Fail(anchor, "RST2002", "A re-exported nominal requires its complete verified original producer identity.");
                }
                else if (layout.NominalScope is not null || layout.NominalSourceName is not null)
                    Fail(anchor, "RST2002", "An alternate nominal owner cannot be supplied without original producer evidence.");
                string identity = ImportedName(layout.NominalSourceName ?? layout.Name, layout.NominalScope ?? sourceScope, anchor);
                var key = (sourceScope, layout.Name);
                if (_importedNominalNames.TryGetValue(key, out string? previous) && previous != identity)
                    Fail(anchor, "RST2002", "The same exported source name is bound to different verified nominal owners.");
                _importedNominalNames[key] = identity;

                static bool Hash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
            }
        }

        private string ImportedName(string name, string scope, SafeCoreHirNode anchor)
        {
            Step(anchor, 0);
            if (string.IsNullOrWhiteSpace(scope) || scope.Length > 4096) Fail(anchor, "RST2002", "The imported nominal scope is missing or unbounded.");
            return scope + "::" + (name.StartsWith("crate::", StringComparison.Ordinal) ? name[7..] : name);
        }

        private static void ExternalNameVisible(SafeCoreHirNode node)
        {
            SafeCoreExternalType? imported = node.ReferencedSymbol?.ExternalType ?? node.ReferencedSymbol?.ExternalEnumVariant?.EnumType;
            if (imported is { IsPublic: false }) Fail(node, "RST2002", "The imported type is private to its producer.");
        }

        private SafeCoreType RemapImportedType(SafeCoreType type, string scope, SafeCoreHirNode anchor, int depth = 0)
        {
            Step(anchor, depth);
            return type.Kind switch
            {
                K.Adt => SafeCoreType.Adt(ResolveImportedName(type.Name!, scope, anchor)),
                K.Reference => SafeCoreType.Reference(RemapImportedType(type.ElementType, scope, anchor, depth + 1), type.IsMutable),
                K.Slice => SafeCoreType.Slice(RemapImportedType(type.ElementType, scope, anchor, depth + 1)),
                K.Array => SafeCoreType.Array(RemapImportedType(type.ElementType, scope, anchor, depth + 1), type.Length!.Value),
                K.Tuple => SafeCoreType.Tuple(type.Elements.Select(element => RemapImportedType(element, scope, anchor, depth + 1)).ToArray()),
                _ => type,
            };
        }

        private string ResolveImportedName(string name, string scope, SafeCoreHirNode anchor) =>
            _importedNominalNames.TryGetValue((scope, name), out string? identity) ? identity : ImportedName(name, scope, anchor);

        private void RegisterExternalLayout(SafeCoreExternalValueType layout, string scope, SafeCoreHirNode anchor,
            SafeCoreExternalTypeKind? exportedKind = null, bool isPublic = false)
        {
            Step(anchor, 0);
            string identity = ResolveImportedName(layout.Name, scope, anchor);
            SafeCoreExternalTypeKind kind = exportedKind ?? layout.Kind;
            if (layout.Fields.IsDefault || layout.Fields.Length > 1024 || layout.Variants.IsDefault || layout.Variants.Length > 1024)
                Fail(anchor, "RST2002", "The imported field or enum variant layout is incomplete or unbounded.");
            if (_importedLayouts.TryGetValue(identity, out SafeCoreMirAdtLayout? previous))
            {
                if (previous.ExternalAssemblyName != layout.AssemblyName || previous.ExternalClrName != layout.ClrName ||
                    previous.IsCopy != layout.IsCopy || previous.Fields.Count != layout.Fields.Length ||
                    previous.Variants.Count != layout.Variants.Length || _importedKinds[identity] != kind ||
                    !SameDrop(previous.ExternalDropFunction, layout.DropFunction))
                    Fail(anchor, "RST2002", "Imported nominal evidence disagrees with its previously registered producer layout.");
                for (int index = 0; index < layout.Fields.Length; index++)
                {
                    Step(anchor, 0);
                    SafeCoreExternalField field = layout.Fields[index];
                    if (previous.Fields[index].Name != field.Name || previous.Fields[index].RequiresStaticLifetime != field.RequiresStaticLifetime ||
                        _importedEvidence[identity].Fields[index].IsPublic != field.IsPublic ||
                        previous.Fields[index].Type != RemapImportedType(ParseImportedType(field.Type, anchor), scope, anchor))
                        Fail(anchor, "RST2002", "Imported nominal fields disagree with the producer evidence already registered.");
                }
                for (int index = 0; index < layout.Variants.Length; index++)
                {
                    Step(anchor, 0);
                    SafeCoreExternalVariant variant = layout.Variants[index];
                    SafeCoreMirAdtVariant registered = previous.Variants[index];
                    if (registered.Name != identity + "::" + variant.SourceName || registered.Discriminant != variant.Discriminant ||
                        registered.FieldOffset != variant.FieldOffset || registered.Fields.Count != variant.Fields.Length ||
                        _importedEvidence[identity].Variants[index].Kind != variant.Kind)
                        Fail(anchor, "RST2002", "Imported enum variants disagree with the producer evidence already registered.");
                    for (int fieldIndex = 0; fieldIndex < variant.Fields.Length; fieldIndex++)
                    {
                        Step(anchor, 0);
                        SafeCoreExternalField field = variant.Fields[fieldIndex];
                        if (registered.Fields[fieldIndex].Name != field.Name || registered.Fields[fieldIndex].RequiresStaticLifetime != field.RequiresStaticLifetime ||
                            _importedEvidence[identity].Variants[index].Fields[fieldIndex].IsPublic != field.IsPublic ||
                            registered.Fields[fieldIndex].Type != RemapImportedType(ParseImportedType(field.Type, anchor), scope, anchor))
                            Fail(anchor, "RST2002", "Imported enum payload fields disagree with the producer evidence already registered.");
                    }
                }
                return;
            }
            SafeCoreType nominal = SafeCoreType.Adt(identity);
            var fields = new List<SafeCoreMirAdtField>();
            var variants = new List<SafeCoreMirAdtVariant>();
            var shapes = new List<AdtShape>();
            SafeCoreMirSource source = new(_hir.SourcePath, anchor.Span, anchor.Id, _hir.Root!.Span.End);
            foreach (SafeCoreExternalField field in layout.Fields)
            {
                Step(anchor, 0);
                fields.Add(MirField(field));
            }
            if (kind == SafeCoreExternalTypeKind.Enum)
            {
                if (layout.Variants.Length == 0 || fields.Count == 0 || fields[0].Name != "$tag" || fields[0].Type.Kind != K.I32)
                    Fail(anchor, "RST2002", "An imported enum requires actual tag and variant layout evidence.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                var tags = new HashSet<int>();
                int offset = 1;
                for (int index = 0; index < layout.Variants.Length; index++)
                {
                    Step(anchor, 0);
                    SafeCoreExternalVariant variant = layout.Variants[index];
                    if (variant.Fields.IsDefault || variant.Fields.Length > 1024 || string.IsNullOrWhiteSpace(variant.Name) ||
                        variant.Name.Length > 1024 || !names.Add(variant.Name) || !tags.Add(variant.Discriminant) || variant.FieldOffset != offset ||
                        variant.Kind == SafeCoreExternalTypeKind.Enum || variant.Kind == SafeCoreExternalTypeKind.UnitStruct && variant.Fields.Length != 0)
                        Fail(anchor, "RST2002", "The imported enum variant evidence is incomplete or inconsistent.");
                    var payload = new List<SafeCoreMirAdtField>();
                    foreach (SafeCoreExternalField field in variant.Fields)
                    {
                        Step(anchor, 0);
                        SafeCoreMirAdtField lowered = MirField(field);
                        if (offset >= fields.Count || fields[offset].Name != "$v" + index + "$" + lowered.Name ||
                            fields[offset].Type != lowered.Type || fields[offset].RequiresStaticLifetime != lowered.RequiresStaticLifetime)
                            Fail(anchor, "RST2002", "The imported enum payload does not match its physical producer layout.");
                        payload.Add(lowered);
                        offset++;
                    }
                    variants.Add(new(identity + "::" + variant.SourceName, variant.Discriminant, variant.FieldOffset, payload, source, _cancellation));
                    shapes.Add(Shape(identity + "::" + variant.SourceName, variant.SourceName, variant.Kind, variant.Fields, N.EnumVariant));
                }
                if (offset != fields.Count) Fail(anchor, "RST2002", "The imported enum contains fields outside its declared variants.");
            }
            else
            {
                if (layout.Variants.Length != 0 || kind == SafeCoreExternalTypeKind.UnitStruct && fields.Count != 0)
                    Fail(anchor, "RST2002", "Imported struct constructor evidence disagrees with its producer layout.");
                shapes.Add(Shape(identity, identity[(identity.LastIndexOf("::", StringComparison.Ordinal) + 2)..], kind, layout.Fields, N.Struct));
            }
            _importedLayouts.Add(identity, new(nominal, fields, variants, source, layout.IsCopy, _cancellation)
            { ExternalAssemblyName = layout.AssemblyName, ExternalClrName = layout.ClrName,
                ExternalDropFunction = layout.DropFunction, IsUnitStruct = kind == SafeCoreExternalTypeKind.UnitStruct,
                ExternalSourceLayout = layout, ExternalSourceIsPublic = isPublic });
            _importedKinds.Add(identity, kind);
            _importedEvidence.Add(identity, layout);
            _adts.Add(identity, shapes);

            SafeCoreMirAdtField MirField(SafeCoreExternalField field) =>
                new(field.Name, RemapImportedType(ParseImportedType(field.Type, anchor), scope, anchor), source)
                { RequiresStaticLifetime = field.RequiresStaticLifetime };

            AdtShape Shape(string qualifiedName, string name, SafeCoreExternalTypeKind constructorKind,
                IReadOnlyList<SafeCoreExternalField> declaredFields, N nodeKind)
            {
                var shapeFields = new List<Field>();
                foreach (SafeCoreExternalField field in declaredFields)
                {
                    Step(anchor, 0);
                    bool fieldPublic = nodeKind == N.EnumVariant || field.IsPublic;
                    var fieldSymbol = new SafeCoreSymbol(field.Name, qualifiedName + "::" + field.Name, SafeCoreSymbolKind.Field,
                        SafeCoreSymbolNamespace.Value, fieldPublic, false, null, anchor.Span, scope);
                    var fieldNode = new SafeCoreHirNode(anchor.Id, N.Field, anchor.Span, field.Name, null,
                        fieldPublic ? SafeCoreHirNodeModifiers.Public : SafeCoreHirNodeModifiers.None, fieldSymbol, null, []);
                    shapeFields.Add(new(fieldNode, RemapImportedType(ParseImportedType(field.Type, anchor), scope, anchor)) { IsImported = true });
                }
                var modifiers = isPublic ? SafeCoreHirNodeModifiers.Public : SafeCoreHirNodeModifiers.None;
                if (constructorKind == SafeCoreExternalTypeKind.TupleStruct) modifiers |= SafeCoreHirNodeModifiers.TupleStruct;
                if (constructorKind == SafeCoreExternalTypeKind.UnitStruct) modifiers |= SafeCoreHirNodeModifiers.UnitStruct;
                var symbol = new SafeCoreSymbol(name, qualifiedName,
                    nodeKind == N.EnumVariant ? SafeCoreSymbolKind.EnumVariant : SafeCoreSymbolKind.Struct,
                    SafeCoreSymbolNamespace.Type, isPublic, false, null, anchor.Span, scope);
                var shapeNode = new SafeCoreHirNode(anchor.Id, nodeKind, anchor.Span, name, null, modifiers, symbol, null, []);
                return new(shapeNode, nominal, shapeFields);
            }

            static bool SameDrop(SafeCoreExternalFunction? first, SafeCoreExternalFunction? second)
            {
                if (first is null || second is null) return first is null && second is null;
                return first.SourceQualifiedName == second.SourceQualifiedName && first.ClrName == second.ClrName &&
                    first.Signature == second.Signature && first.AssemblyName == second.AssemblyName && first.AssemblyPath == second.AssemblyPath &&
                    first.IsPublic == second.IsPublic && first.ClrNamespace == second.ClrNamespace && first.ClrTypeName == second.ClrTypeName &&
                    first.CallPanicStrategy == second.CallPanicStrategy && first.CallReturnContract == second.CallReturnContract &&
                    first.SourceSchema == second.SourceSchema && first.SourceReturnType == second.SourceReturnType && first.NominalScope == second.NominalScope &&
                    first.CallParameterContracts.SequenceEqual(second.CallParameterContracts) && first.SourceParameterTypes.SequenceEqual(second.SourceParameterTypes) &&
                    first.SourceParameterStaticLifetimes.SequenceEqual(second.SourceParameterStaticLifetimes) && first.ReturnOrigins.SequenceEqual(second.ReturnOrigins);
            }
        }

        private void RegisterExternalStructural(SafeCoreExternalStructuralType structural, SafeCoreHirNode anchor)
        {
            Step(anchor, 0);
            SafeCoreType type = RemapImportedType(ParseImportedType(structural.SourceType, anchor), structural.NominalScope, anchor);
            if (type.Kind is not (K.Unit or K.Tuple or K.Array) || string.IsNullOrWhiteSpace(structural.AssemblyName) ||
                structural.AssemblyName.Length > 256 || string.IsNullOrWhiteSpace(structural.ClrName) || structural.ClrName.Length > 4096)
                Fail(anchor, "RST2002", "An imported structural aggregate requires its actual bounded producer CLR identity.");
            if (structural.Owner is { } owner)
            {
                SafeCoreType original = ParseImportedType(owner.SourceName, anchor);
                if (owner.AssemblyName != structural.AssemblyName || owner.ClrName != structural.ClrName ||
                    owner.ModuleVersionId == Guid.Empty || !Hash(owner.SourceSha256) || !Hash(owner.AssemblySha256) ||
                    original.Kind != type.Kind || original.Elements.Count != type.Elements.Count || original.Length != type.Length)
                    Fail(anchor, "RST2002", "An imported structural owner requires its complete original shape and verified producer identity.");
            }
            if (_importedStructuralTypes.TryGetValue(type, out SafeCoreMirImportedStructuralType? previous))
            {
                if (previous.AssemblyName != structural.AssemblyName || previous.ClrName != structural.ClrName)
                    Fail(anchor, "RST2002", "The same anonymous source aggregate is imported from different CLR owners; a checked conversion is required.");
                if (previous.Owner is not null && structural.Owner is not null && previous.Owner != structural.Owner)
                    Fail(anchor, "RST2002", "The same anonymous CLR owner has conflicting original producer proofs.");
                if (previous.Owner is null && structural.Owner is not null)
                    _importedStructuralTypes[type] = previous with { Owner = structural.Owner };
                return;
            }
            _importedStructuralTypes.Add(type, new(type, structural.AssemblyName, structural.ClrName,
                new(_hir.SourcePath, anchor.Span, anchor.Id, _hir.Root!.Span.End)) { Owner = structural.Owner });

            static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
        }

        private SafeCoreType ExternalType(SafeCoreExternalFunction external, SafeCoreHirNode node)
        {
            Step(node, 0);
            if (external.SourceSchema == "rustsharp-source-call-v1")
            {
                if (external.SourceParameterTypes.Length > 128 || external.SourceReturnType is null)
                    Fail(node, "RST2002", "The imported source signature is incomplete.");
                SafeCoreType result = SafeCoreType.Function(external.SourceParameterTypes.Select(text =>
                    RemapImportedType(ParseImportedType(text, node), external.NominalScope ?? "", node)).ToArray(),
                    RemapImportedType(ParseImportedType(external.SourceReturnType, node), external.NominalScope ?? "", node), external.SourceQualifiedName);
                foreach (SafeCoreType part in result.Elements) ValidateNominal(part, 0);
                return result;

                void ValidateNominal(SafeCoreType type, int depth)
                {
                    Step(node, depth);
                    if (type.Kind == K.Adt && !_importedLayouts.ContainsKey(type.Name!))
                        Fail(node, "RST2002", "The imported nominal source type has no checked layout.");
                    foreach (SafeCoreType child in type.Elements) ValidateNominal(child, depth + 1);
                }
            }
            string[] parts = external.Signature.Split("->", StringSplitOptions.None);
            if (parts.Length != 2 || external.Signature.Length > 4096) Fail(node, "RST2002", "The imported function signature is invalid.");
            string[] parameters = parts[0].Length == 0 ? [] : parts[0].Split(',');
            if (parameters.Length > 128) Fail(node, "RST0002", "The imported function exceeds its parameter limit.");
            var types = new List<SafeCoreType>(parameters.Length);
            foreach (string parameter in parameters) { Step(node, 0); types.Add(Parse(parameter, false)); }
            return SafeCoreType.Function(types, Parse(parts[1], true), external.SourceQualifiedName);

            SafeCoreType Parse(string token, bool allowUnit)
            {
                K kind = token switch { "I32" => K.I32, "Bool" => K.Bool, "Void" when allowUnit => K.Unit, _ => K.Error };
                if (kind == K.Error) Fail(node, "RST2002", "The imported function has no supported source type contract.");
                return SafeCoreType.Primitive(kind);
            }
        }

        private SafeCoreType ParseImportedType(string text, SafeCoreHirNode anchor)
        {
            Step(anchor, 0);
            try { return SafeCoreSourceTypeCodec.Parse(text, _cancellation); }
            catch (ArgumentException) { Fail(anchor, "RST2002", "The imported source type is malformed or outside its bounded schema."); throw; }
        }
    }
}
