using System.Diagnostics;
using System.Text.Json;

namespace RustSharp.Semantics;

public static partial class SafeCoreSourceOriginCodec
{
    /// <summary>Checks every returned reference slot against explicit, ordered parameter/static origins.</summary>
    public static IReadOnlyList<string> ValidateContract(
        IReadOnlyList<SafeCoreType> parameters,
        SafeCoreType returnType,
        IReadOnlyList<string> origins,
        Func<SafeCoreType, IReadOnlyList<(string Name, SafeCoreType Type)>> fields,
        CancellationToken cancellationToken = default) =>
        ValidateContract(parameters, returnType, origins, fields, null, null, cancellationToken);

    /// <summary>Checks reference slots with independently declared static input and field requirements.</summary>
    public static IReadOnlyList<string> ValidateContract(
        IReadOnlyList<SafeCoreType> parameters,
        SafeCoreType returnType,
        IReadOnlyList<string> origins,
        Func<SafeCoreType, IReadOnlyList<(string Name, SafeCoreType Type)>> fields,
        IReadOnlyList<bool>? parameterStaticLifetimes,
        Func<SafeCoreType, IReadOnlyList<(string Name, bool RequiresStaticLifetime)>>? staticFields,
        CancellationToken cancellationToken = default) =>
        ValidateContract(parameters, returnType, origins, fields, parameterStaticLifetimes, staticFields, null, cancellationToken);

    /// <summary>Checks only the payload fields of independently declared possible returned enum variants.</summary>
    public static IReadOnlyList<string> ValidateContract(
        IReadOnlyList<SafeCoreType> parameters,
        SafeCoreType returnType,
        IReadOnlyList<string> origins,
        Func<SafeCoreType, IReadOnlyList<(string Name, SafeCoreType Type)>> fields,
        IReadOnlyList<bool>? parameterStaticLifetimes,
        Func<SafeCoreType, IReadOnlyList<(string Name, bool RequiresStaticLifetime)>>? staticFields,
        Func<SafeCoreType, IReadOnlyList<SafeCoreMirProjection>, IReadOnlyList<(string Name, SafeCoreType Type)>?>? returnedFields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(returnType);
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(fields);
        if (parameters.Count > MaximumItems || origins.Count > MaximumItems)
            return ["Source origin parameter or origin count exceeds its item budget."];
        return new ContractValidator(parameters, returnType, origins, fields, parameterStaticLifetimes, staticFields, returnedFields, cancellationToken).Run();
    }

    private sealed record ReferenceSlot(IReadOnlyList<SafeCoreMirProjection> Path, SafeCoreType Type);

    private sealed class ContractValidator(
        IReadOnlyList<SafeCoreType> parameters,
        SafeCoreType returnType,
        IReadOnlyList<string> origins,
        Func<SafeCoreType, IReadOnlyList<(string Name, SafeCoreType Type)>> fields,
        IReadOnlyList<bool>? parameterStaticLifetimes,
        Func<SafeCoreType, IReadOnlyList<(string Name, bool RequiresStaticLifetime)>>? staticFields,
        Func<SafeCoreType, IReadOnlyList<SafeCoreMirProjection>, IReadOnlyList<(string Name, SafeCoreType Type)>?>? returnedFields,
        CancellationToken cancellationToken)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<string> _diagnostics = [];
        private readonly List<ReferenceSlot> _slots = [];
        private int _operations;

        public System.Collections.ObjectModel.ReadOnlyCollection<string> Run()
        {
            try
            {
                CollectSlots(returnType, [], 0);
                var covered = new bool[_slots.Count];
                foreach (string term in origins)
                {
                    Step();
                    SafeCoreMirReferenceOrigin origin = Parse(term, cancellationToken);
                    int slotIndex = _slots.FindIndex(slot => slot.Path.SequenceEqual(origin.ValuePath));
                    if (slotIndex < 0)
                    {
                        Error("Source return origin names a missing or non-reference value slot.");
                        continue;
                    }
                    covered[slotIndex] = true;
                    SafeCoreType returned = _slots[slotIndex].Type;
                    bool legacy = !term.StartsWith(Prefix, StringComparison.Ordinal);
                    if (!legacy && origin.IsMutable != returned.IsMutable)
                        Error("Source return origin mutability contradicts its returned reference slot.");
                    if (origin.HasUnknownSliceOffset && returned.ElementType.Kind != SafeCoreSemanticTypeKind.Slice)
                        Error("Unknown source slice offsets require a returned slice reference.");
                    if (origin.IsStatic)
                    {
                        if (returned.IsMutable && !origin.IsParameter) Error("Mutable source references cannot originate in shared static storage.");
                        if (!origin.IsParameter) continue;
                    }
                    if (!origin.IsParameter || (uint)origin.LocalId >= (uint)parameters.Count)
                    {
                        Error("Source return origin references a missing parameter.");
                        continue;
                    }
                    if (origin.IsStatic && !RequiresStaticPath(origin.LocalId, origin.ParameterPath))
                        Error("Static source return origins require an independently declared static input slot.");
                    (SafeCoreType input, bool mutablePath) = Resolve(parameters[origin.LocalId], origin.ParameterPath, true);
                    if (input.Kind != SafeCoreSemanticTypeKind.Reference)
                    {
                        Error("Source parameter origin must select an initialized input reference slot.");
                        continue;
                    }
                    (SafeCoreType referent, bool mutableReferent) = Resolve(input.ElementType, origin.Projections, input.IsMutable && mutablePath);
                    bool compatible = referent == returned.ElementType ||
                        returned.ElementType.Kind == SafeCoreSemanticTypeKind.Slice &&
                        referent.Kind is SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice &&
                        referent.ElementType == returned.ElementType.ElementType;
                    if (!compatible)
                        Error("Source parameter origin projection contradicts its returned referent type.");
                    if (returned.IsMutable && !mutableReferent)
                        Error("Mutable source return origin traverses shared input storage.");
                }
                for (int index = 0; index < covered.Length; index++)
                {
                    Step();
                    if (!covered[index]) Error("Source return origins do not cover every returned reference slot.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or
                InvalidOperationException or OverflowException)
            {
                Error("Invalid source origin contract: " + exception.Message);
            }
            return _diagnostics.AsReadOnly();
        }

        private void CollectSlots(SafeCoreType type, IReadOnlyList<SafeCoreMirProjection> path, int depth)
        {
            Step();
            if (depth >= MaximumDepth) throw Invalid("Source reference slot depth exceeded.");
            if (type.Kind == SafeCoreSemanticTypeKind.Reference)
            {
                if (_slots.Count >= MaximumItems) throw Invalid("Source reference slot count exceeded.");
                _slots.Add(new(path, type));
                return;
            }
            if (type.Kind == SafeCoreSemanticTypeKind.Tuple)
                for (int index = 0; index < type.Elements.Count; index++)
                {
                    Step();
                    CollectSlots(type.Elements[index], [.. path, SafeCoreMirProjection.TupleIndex(index)], depth + 1);
                }
            if (type.Kind == SafeCoreSemanticTypeKind.Array)
            {
                if (type.Length is null or < 0 or > MaximumItems) throw Invalid("Source array origin length exceeded.");
                for (int index = 0; index < type.Length; index++)
                {
                    Step();
                    CollectSlots(type.ElementType, [.. path, SafeCoreMirProjection.ArrayIndex(index)], depth + 1);
                }
            }
            if (type.Kind == SafeCoreSemanticTypeKind.Adt)
            {
                IReadOnlyList<(string Name, SafeCoreType Type)> declared = returnedFields?.Invoke(type, path) ?? fields(type);
                if (declared.Count > MaximumItems) throw Invalid("Source origin field count exceeded.");
                foreach ((string name, SafeCoreType fieldType) in declared)
                {
                    Step();
                    CollectSlots(fieldType, [.. path, SafeCoreMirProjection.Field(name)], depth + 1);
                }
            }
        }

        private (SafeCoreType Type, bool Mutable) Resolve(SafeCoreType type,
            IReadOnlyList<SafeCoreMirProjection> path, bool mutable)
        {
            if (path.Count > MaximumDepth) throw Invalid("Source origin projection depth exceeded.");
            foreach (SafeCoreMirProjection projection in path)
            {
                Step();
                switch (projection.Kind)
                {
                    case SafeCoreMirProjectionKind.Dereference:
                        if (type.Kind != SafeCoreSemanticTypeKind.Reference) throw Invalid("Source origin dereferences a non-reference.");
                        mutable &= type.IsMutable;
                        type = type.ElementType;
                        break;
                    case SafeCoreMirProjectionKind.TupleIndex:
                        if (type.Kind != SafeCoreSemanticTypeKind.Tuple || (uint)projection.Index >= (uint)type.Elements.Count)
                            throw Invalid("Source origin tuple projection is out of range.");
                        type = type.Elements[projection.Index];
                        break;
                    case SafeCoreMirProjectionKind.Field:
                        if (type.Kind != SafeCoreSemanticTypeKind.Adt) throw Invalid("Source origin selects a field on a non-ADT.");
                        IReadOnlyList<(string Name, SafeCoreType Type)> declared = fields(type);
                        if (declared.Count > MaximumItems) throw Invalid("Source origin field count exceeded.");
                        int index = -1;
                        for (int fieldIndex = 0; fieldIndex < declared.Count; fieldIndex++)
                        {
                            Step();
                            if (declared[fieldIndex].Name == projection.Name) { index = fieldIndex; break; }
                        }
                        if (index < 0) throw Invalid("Source origin selects an undeclared field.");
                        type = declared[index].Type;
                        break;
                    case SafeCoreMirProjectionKind.ArrayIndex:
                    case SafeCoreMirProjectionKind.DynamicIndex:
                    case SafeCoreMirProjectionKind.FromEndIndex:
                        if (type.Kind is not (SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice) ||
                            type.Kind == SafeCoreSemanticTypeKind.Array &&
                            (projection.Kind == SafeCoreMirProjectionKind.ArrayIndex && projection.Index >= type.Length ||
                             projection.Kind == SafeCoreMirProjectionKind.FromEndIndex && projection.MinimumLength > type.Length))
                            throw Invalid("Source origin array/slice projection is incompatible or out of range.");
                        type = type.ElementType;
                        break;
                    default: throw Invalid("Source origin projection needs an unsupported nominal variant descriptor.");
                }
            }
            return (type, mutable);
        }

        private bool RequiresStaticPath(int parameter, IReadOnlyList<SafeCoreMirProjection> path)
        {
            SafeCoreType type = parameters[parameter];
            bool required = parameterStaticLifetimes is not null && parameter < parameterStaticLifetimes.Count &&
                parameterStaticLifetimes[parameter];
            foreach (SafeCoreMirProjection projection in path)
            {
                Step();
                if (projection.Kind == SafeCoreMirProjectionKind.Field && type.Kind == SafeCoreSemanticTypeKind.Adt)
                {
                    required = staticFields?.Invoke(type).Any(field => field.Name == projection.Name && field.RequiresStaticLifetime) == true;
                }
                else required = false;
                type = Resolve(type, [projection], true).Type;
            }
            return required;
        }

        private void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_operations > 65536 || _clock.Elapsed > TimeSpan.FromSeconds(5))
                throw Invalid("Source origin validation exceeded its operation or time budget.");
        }

        private void Error(string message)
        {
            if (_diagnostics.Count < MaximumItems) _diagnostics.Add(message);
        }
    }
}
