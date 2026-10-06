using System.Globalization;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirValidation
{
    private sealed partial class Validator
    {
        private void ValidateCleanupDiscriminant(SafeCoreMirStatement statement)
        {
            if (!CheckedCleanupDiscriminant(statement))
                Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow,
                    "A cleanup discriminant must feed only exact variant tests and cleanup of that owner's payload before its join.",
                    statement.Source);
        }

        private bool CheckedCleanupDiscriminant(SafeCoreMirStatement marker)
        {
            Step();
            SafeCoreMirRvalue value = marker.Value;
            if (!marker.IsCleanupDiscriminant || marker.CleanupDiscriminantJoinBlockId is not int join ||
                join < 0 || join >= _function!.Blocks.Count || value.Kind != SafeCoreMirRvalueKind.Discriminant ||
                value.Operands.Count != 1 || value.Operands[0].Place is not { } owner ||
                marker.DestinationPlace is not null || marker.DestinationLocalId < 0 ||
                marker.DestinationLocalId >= _function.Locals.Count ||
                _function.Locals[marker.DestinationLocalId].Kind != SafeCoreMirLocalKind.Temporary ||
                value.Type.Kind != SafeCoreSemanticTypeKind.I32 || value.Operands[0].Type.Name is not { } name ||
                !_adtLayouts.TryGetValue(name, out SafeCoreMirAdtLayout? layout) || layout.Variants.Count == 0)
                return false;
            int definitions = 0;
            int tests = 0;
            foreach (SafeCoreMirBlock block in _function.Blocks)
            {
                Step();
                if (Mentions(block.Terminator.Operand, marker.DestinationLocalId) ||
                    block.Terminator.DestinationLocalId == marker.DestinationLocalId) return false;
                foreach (SafeCoreMirOperand argument in block.Terminator.Arguments)
                { Step(); if (Mentions(argument, marker.DestinationLocalId)) return false; }
                foreach (SafeCoreMirStatement statement in block.Statements)
                {
                    Step();
                    if (statement.DestinationLocalId == marker.DestinationLocalId) definitions++;
                    foreach (SafeCoreMirOperand operand in statement.Value.Operands)
                    {
                        Step();
                        if (!Mentions(operand, marker.DestinationLocalId)) continue;
                        if (!CleanupVariantTest(statement, block, marker, layout, out int variant)) return false;
                        if (!CleanupOnlyRegion(block.Terminator.TargetBlockId, join,
                                owner.Append(SafeCoreMirProjection.Downcast(variant)))) return false;
                        if (!CleanupOnlyRegion(block.Terminator.FalseTargetBlockId, join, owner,
                                requireDestructor: false)) return false;
                        tests++;
                    }
                }
            }
            return definitions == 1 && tests > 0;
        }

        private bool CleanupVariantTest(SafeCoreMirStatement statement, SafeCoreMirBlock block,
            SafeCoreMirStatement marker, SafeCoreMirAdtLayout layout, out int variant)
        {
            Step();
            variant = -1;
            SafeCoreMirRvalue test = statement.Value;
            if (test.Kind != SafeCoreMirRvalueKind.Binary || test.Operator != "==" || test.Operands.Count != 2 ||
                test.Operands[0].Kind != SafeCoreMirOperandKind.Local || test.Operands[0].Id != marker.DestinationLocalId ||
                test.Operands[1].Kind != SafeCoreMirOperandKind.Constant ||
                !int.TryParse(test.Operands[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tag) ||
                test.Type.Kind != SafeCoreSemanticTypeKind.Bool || statement.DestinationPlace is not null ||
                statement.Source != marker.Source || block.Terminator.Kind != SafeCoreMirTerminatorKind.Branch ||
                block.Terminator.Operand is not { Kind: SafeCoreMirOperandKind.Local } condition ||
                condition.Id != statement.DestinationLocalId || condition.Type.Kind != SafeCoreSemanticTypeKind.Bool)
                return false;
            for (int index = 0; index < layout.Variants.Count; index++)
            { Step(); if (layout.Variants[index].Discriminant == tag) { variant = index; break; } }
            if (variant < 0) return false;
            int branchUses = 0;
            int definitions = 0;
            foreach (SafeCoreMirBlock candidate in _function!.Blocks)
            {
                Step();
                if (candidate.Terminator.DestinationLocalId == condition.Id) return false;
                if (Mentions(candidate.Terminator.Operand, condition.Id))
                {
                    if (candidate.Id != block.Id || candidate.Terminator.Kind != SafeCoreMirTerminatorKind.Branch) return false;
                    branchUses++;
                }
                foreach (SafeCoreMirOperand argument in candidate.Terminator.Arguments)
                { Step(); if (Mentions(argument, condition.Id)) return false; }
                foreach (SafeCoreMirStatement effect in candidate.Statements)
                {
                    Step();
                    if (effect.DestinationLocalId == condition.Id) definitions++;
                    foreach (SafeCoreMirOperand operand in effect.Value.Operands)
                    { Step(); if (Mentions(operand, condition.Id)) return false; }
                }
            }
            return branchUses == 1 && definitions == 1;
        }

        private bool CleanupOnlyRegion(int entry, int join, SafeCoreMirPlace prefix, bool requireDestructor = true)
        {
            var pending = new Stack<int>();
            var seen = new HashSet<int>();
            pending.Push(entry);
            bool reachedJoin = false;
            bool hasDestructor = false;
            for (int visits = 0; pending.Count != 0 && visits <= _function!.Blocks.Count * 2; visits++)
            {
                Step();
                int id = pending.Pop();
                if (id == join) { reachedJoin = true; continue; }
                if (id < 0 || id >= _function.Blocks.Count) return false;
                if (!seen.Add(id)) continue;
                SafeCoreMirBlock block = _function.Blocks[id];
                foreach (SafeCoreMirStatement statement in block.Statements)
                {
                    Step();
                    SafeCoreMirRvalue value = statement.Value;
                    if (statement.IsCleanupDiscriminant && value.Kind == SafeCoreMirRvalueKind.Discriminant &&
                        value.Operands.Count == 1 && PlaceWithin(value.Operands[0].Place, prefix)) continue;
                    if (value.Kind == SafeCoreMirRvalueKind.Binary && value.Operator == "==" && value.Operands.Count == 2 &&
                        block.Terminator.Kind == SafeCoreMirTerminatorKind.Branch &&
                        block.Terminator.Operand?.Id == statement.DestinationLocalId &&
                        FindCleanupTag(value.Operands[0], prefix) is not null) continue;
                    if (value.Kind == SafeCoreMirRvalueKind.Unary && value.Operator == "&mut" && value.Operands.Count == 1 &&
                        PlaceWithin(value.Operands[0].Place, prefix) &&
                        block.Terminator.Kind == SafeCoreMirTerminatorKind.Call &&
                        block.Terminator.Arguments.Count == 1 && block.Terminator.Arguments[0].Id == statement.DestinationLocalId)
                        continue;
                    return false;
                }
                SafeCoreMirTerminator terminator = block.Terminator;
                if (terminator.Kind == SafeCoreMirTerminatorKind.Goto) pending.Push(terminator.TargetBlockId);
                else if (terminator.Kind == SafeCoreMirTerminatorKind.Branch)
                {
                    pending.Push(terminator.TargetBlockId);
                    pending.Push(terminator.FalseTargetBlockId);
                }
                else if (terminator.Kind == SafeCoreMirTerminatorKind.Call &&
                    terminator.Operand is { Kind: SafeCoreMirOperandKind.Function } target &&
                    program.IsDestructorFunction(target.Id))
                {
                    SafeCoreMirBlock? saved = _block;
                    try { _block = block; if (!HasMatchingDropReceiver(terminator, terminator.DropLocalId)) return false; }
                    finally { _block = saved; }
                    hasDestructor = true;
                    pending.Push(terminator.TargetBlockId);
                }
                else return false;
            }
            return pending.Count == 0 && reachedJoin && (!requireDestructor || hasDestructor);
        }

        private SafeCoreMirStatement? FindCleanupTag(SafeCoreMirOperand operand, SafeCoreMirPlace prefix)
        {
            if (operand.Kind != SafeCoreMirOperandKind.Local) return null;
            foreach (SafeCoreMirBlock block in _function!.Blocks)
                foreach (SafeCoreMirStatement statement in block.Statements)
                {
                    Step();
                    if (statement.DestinationLocalId == operand.Id && statement.IsCleanupDiscriminant &&
                        statement.Value.Operands.Count == 1 && PlaceWithin(statement.Value.Operands[0].Place, prefix))
                        return statement;
                }
            return null;
        }

        private bool PlaceWithin(SafeCoreMirPlace? place, SafeCoreMirPlace prefix)
        {
            if (place is null || place.LocalId != prefix.LocalId || place.Projections.Count < prefix.Projections.Count) return false;
            for (int index = 0; index < prefix.Projections.Count; index++)
            { Step(); if (place.Projections[index] != prefix.Projections[index]) return false; }
            return true;
        }

        private static bool Mentions(SafeCoreMirOperand? operand, int localId) =>
            operand is { Kind: SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place } used && used.Id == localId;
    }
}
