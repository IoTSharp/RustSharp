using System.Diagnostics;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P1DropReceiverTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 Drop receiver retains mutable self and owned field MIR places", ReceiverPlacesAsync),
        new("P1 Drop receiver invalid signatures diagnose stably", InvalidReceiversAsync),
        new("P1 Drop receiver mutable access is restricted to checked destructor calls", RejectsForgedReceiverAsync),
        new("P1 Drop receiver generated bodies read and mutate owned fields", GeneratedReceiverAsync),
        new("P1 Drop receiver cleanup skips moved fields and records live projected obligations", PartialMoveCleanupAsync),
        new("P1 Drop receiver conditional cleanup preserves user borrow and use-after-drop errors", ConditionalCleanupSafetyAsync),
        new("P1 Drop receiver cleanup evidence rejects changed conditional flags", ConditionalEvidenceAsync),
        new("P1 Drop receiver generated body drops local owners before owned fields", GeneratedCompositeReceiverAsync),
        new("P1 Drop receiver guarded enum cleanup skips uninitialized and moved storage", GeneratedEnumGuardsAsync),
        new("P1 Drop receiver MIR snapshots bind destructor identity and panic policy", CleanupSnapshotBindingAsync),
        new("P1 Drop receiver enum facts select the actual moved variant and reject evidence drift", EnumKnownFactsAsync),
        new("P1 Drop receiver mutable owner borrows invalidate exact branch facts", MutableKnownFactsAsync),
        new("P1 Drop receiver replacement moves create a fresh root or field generation after cleanup", ReplacementGenerationsAsync),
        new("P1 Drop receiver cleanup guards reject writes, forged signatures, and invalid bool results", CleanupGuardsAsync),
        new("P1 Drop receiver borrowed local aliases share enum replacement and panic consumption", BorrowedAliasGenerationsAsync),
    ];

    private const string Source = """
        struct Owner { value: i32 }
        impl Drop for Owner {
            fn drop(&mut self) {
                self.value += 1;
                println!("{}", self.value);
            }
        }
        fn main() { let owner = Owner { value: 7 }; println!("body"); }
        """;

    private static Task ReceiverPlacesAsync()
    {
        SafeCoreMirPipelineResult result = Analyze(Source);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreHirNode declaration = result.Hir!.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.IdentifierPattern && node.Name == "self");
        SafeCoreHirNode[] receiverUses = result.Hir.Nodes.Where(node =>
            node.Kind == SafeCoreHirNodeKind.NameExpression && node.Name == "self").ToArray();
        AssertEx.True(declaration.DeclaredSymbol is { Kind: SafeCoreSymbolKind.Parameter } &&
            receiverUses.Length == 2 && receiverUses.All(node =>
                node.ReferencedSymbol == declaration.DeclaredSymbol),
            "Every source self use must bind to the declared receiver parameter through HIR.");
        SafeCoreMirProgram mir = result.Mir!.Program!;
        SafeCoreMirFunction destructor = mir.Functions.Single(function => function.IsDestructor);
        SafeCoreMirLocal receiver = destructor.Locals.Single(local => local.Kind == SafeCoreMirLocalKind.Parameter);
        AssertEx.Equal("self", receiver.Name);
        AssertEx.True(receiver.Type.Kind == SafeCoreSemanticTypeKind.Reference && receiver.Type.IsMutable &&
            receiver.Type.ElementType?.Kind == SafeCoreSemanticTypeKind.Adt,
            "A destructor receiver must retain the nominal owner behind &mut self.");
        SafeCoreMirStatement write = destructor.Blocks.SelectMany(block => block.Statements)
            .Single(statement => statement.DestinationPlace is { IsRoot: false });
        AssertEx.Equal(receiver.Id, write.DestinationPlace!.LocalId);
        AssertEx.True(write.DestinationPlace.Projections.Select(projection => projection.Kind)
            .SequenceEqual([SafeCoreMirProjectionKind.Dereference, SafeCoreMirProjectionKind.Field]),
            "A receiver field write must use its source-correlated dereference/field place.");
        AssertEx.Equal("value", write.DestinationPlace.Projections[^1].Name!);
        SafeCoreMirFunction main = mir.Functions.Single(function => !function.IsDestructor);
        SafeCoreMirLocal owner = main.Locals.Single(local => local.Name == "owner");
        SafeCoreMirTerminator call = main.Blocks.Select(block => block.Terminator)
            .Single(terminator => terminator.DropLocalId == owner.Id);
        AssertEx.True(call.Arguments.Count == 1 && call.Arguments[0].Type == receiver.Type,
            "Generated cleanup must pass exactly the checked mutable receiver.");
        return Task.CompletedTask;
    }

    private static Task InvalidReceiversAsync()
    {
        string[] methods =
        [
            "fn drop(self) {}",
            "fn drop(&self) {}",
            "fn drop(owner: &mut Owner) {}",
            "fn drop(&mut self, other: i32) {}",
            "fn drop(&mut self) -> i32 { 1 }",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        foreach (string method in methods)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Receiver signature corpus exceeded its wall-clock bound.");
            string source = "struct Owner { value: i32 } impl Drop for Owner { " + method + " } fn main() {}";
            SafeCoreMirPipelineResult first = Analyze(source, deadline.Token);
            SafeCoreMirPipelineResult second = Analyze(source, deadline.Token);
            AssertEx.False(first.IsSuccessful || second.IsSuccessful,
                "An invalid Drop receiver must fail before a MIR cleanup program is published: " + method);
            AssertEx.True(first.Diagnostics.Count > 0 && first.Diagnostics.Count <= 16,
                "Invalid receiver diagnostics must stay finite and explicit.");
            AssertEx.Equal(string.Join('\n', first.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)),
                string.Join('\n', second.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
            AssertEx.Equal("p1-drop-receiver.rs", first.Hir!.SourcePath,
                "Rejected frontend evidence must retain its input source path.");
            AssertEx.True(first.Diagnostics.All(diagnostic =>
                (diagnostic.SourcePath is null || diagnostic.SourcePath == first.Hir.SourcePath) &&
                diagnostic.Span.Start >= 0 && diagnostic.Span.End <= source.Length),
                "Invalid receiver diagnostics must retain the original source extent.");
        }
        return Task.CompletedTask;
    }

    private static Task RejectsForgedReceiverAsync()
    {
        SafeCoreMirPipelineResult result = Analyze(Source);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreMirProgram mir = result.Mir!.Program!;
        SafeCoreMirFunction checkedDestructor = mir.Functions.Single(function => function.IsDestructor);
        SafeCoreMirFunction unmarked = new(checkedDestructor.Id, checkedDestructor.Name,
            checkedDestructor.ReturnType, checkedDestructor.Locals, checkedDestructor.Blocks,
            checkedDestructor.EntryBlockId, checkedDestructor.Source, checkedDestructor.IsPublic);
        SafeCoreMirProgram forged = new(mir.Functions.Select(function => function.Id == unmarked.Id ? unmarked : function).ToArray(), mir.AdtLayouts);
        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(forged,
            new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 16_384 });
        AssertEx.False(validation.IsSuccessful,
            "An ordinary callable function cannot borrow immutable storage by posing as a destructor receiver.");
        AssertEx.True(validation.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirDiagnosticCodes.TypeMismatch),
            "The missing checked destructor identity must retain the mutability type diagnostic.");
        return Task.CompletedTask;
    }

    private static async Task GeneratedReceiverAsync()
    {
        await CompileAndRunAsync(Source, "body\n8\n").ConfigureAwait(false);
    }

    private const string PartialMoveSource = """
        struct First;
        impl Drop for First { fn drop(&mut self) { println!("first"); } }
        struct Second;
        impl Drop for Second { fn drop(&mut self) { println!("second"); } }
        struct Fields { first: First, second: Second }
        fn main() {
            let fields = Fields { first: First, second: Second };
            let moved = fields.first;
            println!("body");
        }
        """;

    private static Task PartialMoveCleanupAsync()
    {
        SafeCoreMirPipelineResult result = Analyze(PartialMoveSource);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreMirFunction main = result.Mir!.Program!.Functions.Single(function => !function.IsDestructor);
        SafeCoreOwnershipFunction ownership = result.Ownership!.Program!.Functions.Single(function => function.Name == main.Name);
        SafeCoreOwnershipInstruction[] drops = ownership.Blocks.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Kind == SafeCoreOwnershipInstructionKind.Drop).ToArray();
        AssertEx.True(drops.Length >= 3 && drops.All(instruction => instruction.IsConditionalDrop),
            "Root and field cleanup must retain checked conditional obligations.");
        SafeCoreOwnershipPath path = result.Ownership.Ownership!.Paths.Single(value => value.FunctionName == main.Name);
        AssertEx.Equal("moved,fields.second", string.Join(',', path.DropOrder),
            "Cleanup must skip the moved field while visiting its recipient and remaining sibling once.");
        AssertEx.False(ownership.Blocks.SelectMany(block => block.Instructions).Any(instruction =>
            instruction.Kind == SafeCoreOwnershipInstructionKind.Borrow),
            "Generated conditional receivers must not create unconditional user loans of moved storage.");
        return Task.CompletedTask;
    }

    private static Task ConditionalEvidenceAsync()
    {
        SafeCoreMirPipelineResult result = Analyze(PartialMoveSource);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreOwnershipProgram evidence = result.Ownership!.Program!;
        SafeCoreOwnershipProgram forged = new(evidence.Functions.Select(function => new SafeCoreOwnershipFunction(
            function.Name, function.Locals, function.Scopes,
            function.Blocks.Select(block => new SafeCoreOwnershipBlock(block.Id, block.ScopeId,
                block.Instructions.Select(instruction => instruction.IsConditionalDrop
                    ? instruction with { IsConditionalDrop = false } : instruction).ToArray(),
                block.Terminator, block.Source)).ToArray(), function.EntryBlockId, function.PanicStrategy, function.Source)).ToArray());
        SafeCoreMirOwnershipResult rejected = SafeCoreMirOwnershipAdapter.Analyze(result.Mir!.Program!, forged,
            new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 65_536 });
        AssertEx.False(rejected.IsSuccessful, "Explicit ownership evidence may not erase conditional cleanup semantics.");
        AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "Changed conditional cleanup evidence must fail before executing ownership effects.");
        return Task.CompletedTask;
    }

    private static Task ConditionalCleanupSafetyAsync()
    {
        SafeCoreMirSource source = new("conditional-drop.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreType ownerType = SafeCoreType.Adt("Owner");
        SafeCoreType referenceType = SafeCoreType.Reference(ownerType, false);
        SafeCoreOwnershipPlace owner = SafeCoreOwnershipPlace.Root(0);
        SafeCoreOwnershipInstruction cleanup = SafeCoreOwnershipInstruction.Drop(owner, source) with { IsConditionalDrop = true };
        SafeCoreOwnershipLocal[] locals =
        [
            new(0, "owner", ownerType, SafeCoreOwnershipKind.Move, true, 0, false, true, source),
            new(1, "borrow", referenceType, SafeCoreOwnershipKind.Copy, false, 0, true, false, source),
        ];
        SafeCoreOwnershipAnalysisResult AnalyzeEffects(SafeCoreOwnershipInstruction[] effects) =>
            SafeCoreOwnershipAnalysis.Analyze(new([new SafeCoreOwnershipFunction("cleanup", locals,
                [new SafeCoreOwnershipScope(0, -1, source)],
                [new SafeCoreOwnershipBlock(0, 0, effects, SafeCoreOwnershipTerminator.ReturnUnit(source), source)],
                0, SafeCorePanicStrategy.Unwind, source)]),
                new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 16_384 });
        SafeCoreOwnershipAnalysisResult borrowed = AnalyzeEffects([
            SafeCoreOwnershipInstruction.Borrow(0, 1, false, source), cleanup,
            SafeCoreOwnershipInstruction.Use(1, source)]);
        AssertEx.False(borrowed.IsSuccessful, "Conditional cleanup cannot destroy storage with an active user loan.");
        AssertEx.True(borrowed.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.MoveWhileBorrowed),
            "Live user loans must retain the drop-while-borrowed diagnostic.");
        SafeCoreOwnershipAnalysisResult repeated = AnalyzeEffects([cleanup, cleanup]);
        AssertEx.True(repeated.IsSuccessful, string.Join(Environment.NewLine, repeated.Diagnostics));
        AssertEx.Equal("owner", string.Join(',', repeated.Paths.Single().DropOrder),
            "A cleared cleanup flag must suppress repeated and final-scope destructor calls.");
        SafeCoreOwnershipAnalysisResult used = AnalyzeEffects([cleanup, SafeCoreOwnershipInstruction.Use(0, source)]);
        AssertEx.False(used.IsSuccessful, "The retained field storage cannot revive ordinary access after cleanup.");
        AssertEx.True(used.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove),
            "Post-cleanup access must retain the source ownership error.");
        return Task.CompletedTask;
    }

    private static Task ReplacementGenerationsAsync()
    {
        var source = new SafeCoreMirSource("p1-replacement-generation.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreType owner = SafeCoreType.Adt("Owner");
        SafeCoreOwnershipAnalysisResult AnalyzeEffects(SafeCoreOwnershipLocal[] locals, SafeCoreOwnershipInstruction[] effects) =>
            SafeCoreOwnershipAnalysis.Analyze(new([new SafeCoreOwnershipFunction("replacement", locals,
                [new SafeCoreOwnershipScope(0, -1, source)],
                [new SafeCoreOwnershipBlock(0, 0, effects, SafeCoreOwnershipTerminator.ReturnUnit(source), source)],
                0, SafeCorePanicStrategy.Unwind, source)]),
                new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 16_384 });
        SafeCoreOwnershipLocal[] roots =
        [new(0, "destination", owner, SafeCoreOwnershipKind.Move, true, 0, false, true, source),
         new(1, "rhs", owner, SafeCoreOwnershipKind.Move, true, 0, false, true, source)];
        SafeCoreOwnershipInstruction rootDrop = SafeCoreOwnershipInstruction.Drop(0, source) with { IsConditionalDrop = true };
        SafeCoreOwnershipAnalysisResult root = AnalyzeEffects(roots,
            [rootDrop, SafeCoreOwnershipInstruction.Move(1, 0, source), SafeCoreOwnershipInstruction.Use(0, source), rootDrop]);
        AssertEx.True(root.IsSuccessful, string.Join(Environment.NewLine, root.Diagnostics));
        AssertEx.Equal("destination,destination", string.Join(',', root.Paths.Single().DropOrder),
            "Each old and replacement generation must be consumed once without dropping the moved RHS separately.");
        SafeCoreOwnershipAnalysisResult live = AnalyzeEffects(roots, [SafeCoreOwnershipInstruction.Move(1, 0, source)]);
        AssertEx.False(live.IsSuccessful, "A move cannot overwrite a live generation that has not been cleaned.");
        AssertEx.True(live.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.InvalidDrop),
            "The live replacement destination must retain its original drop-before-overwrite diagnostic.");
        SafeCoreOwnershipPlace field = SafeCoreOwnershipPlace.Root(0).Append(SafeCoreOwnershipProjection.TupleIndex(0));
        SafeCoreOwnershipInstruction fieldDrop = SafeCoreOwnershipInstruction.Drop(field, source) with { IsConditionalDrop = true };
        SafeCoreOwnershipLocal[] fields =
        [new(0, "fields", SafeCoreType.Tuple([owner]), SafeCoreOwnershipKind.Move, false, 0, false, true, source),
         new(1, "rhs", owner, SafeCoreOwnershipKind.Move, true, 0, false, true, source)];
        SafeCoreOwnershipAnalysisResult projected = AnalyzeEffects(fields,
            [fieldDrop, SafeCoreOwnershipInstruction.Move(SafeCoreOwnershipPlace.Root(1), field, source),
             SafeCoreOwnershipInstruction.Use(field, source), fieldDrop]);
        AssertEx.True(projected.IsSuccessful, string.Join(Environment.NewLine, projected.Diagnostics));
        AssertEx.Equal("fields.tuple[0],fields.tuple[0]", string.Join(',', projected.Paths.Single().DropOrder),
            "Projected replacement must reset its generation flag and retain exact field cleanup identity.");
        SafeCoreOwnershipAnalysisResult liveField = AnalyzeEffects([.. fields,
            new(2, "next_rhs", owner, SafeCoreOwnershipKind.Move, true, 0, false, true, source)],
            [fieldDrop, SafeCoreOwnershipInstruction.Move(SafeCoreOwnershipPlace.Root(1), field, source),
             SafeCoreOwnershipInstruction.Move(SafeCoreOwnershipPlace.Root(2), field, source)]);
        AssertEx.False(liveField.IsSuccessful, "A fresh projected generation must retain its pending Drop obligation until it is consumed.");
        AssertEx.True(liveField.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.InvalidDrop),
            "Uncleaned projected replacement must retain the drop-before-overwrite diagnostic.");
        return Task.CompletedTask;
    }

    private static async Task GeneratedCompositeReceiverAsync()
    {
        const string source = """
            struct Child;
            impl Drop for Child { fn drop(&mut self) { println!("child"); } }
            struct Local;
            impl Drop for Local { fn drop(&mut self) { println!("local"); } }
            struct Parent { child: Child }
            impl Drop for Parent {
                fn drop(&mut self) { let local = Local; println!("parent"); }
            }
            fn main() { let parent = Parent { child: Child }; println!("body"); }
            """;
        await CompileAndRunAsync(source, "body\nparent\nlocal\nchild\n").ConfigureAwait(false);
    }

    private static async Task GeneratedEnumGuardsAsync()
    {
        const string uninitialized = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            enum Choice { Owned(Marker), Empty }
            fn main() { let choice: Choice; println!("body"); }
            """;
        SafeCoreMirPipelineResult proof = Analyze(uninitialized);
        AssertEx.True(proof.IsSuccessful, string.Join(Environment.NewLine, proof.Diagnostics));
        SafeCoreMirProgram mir = proof.Mir!.Program!;
        AssertEx.True(mir.Functions.SelectMany(function => function.Blocks).SelectMany(block => block.Statements)
            .Any(statement => statement.IsCleanupDiscriminant && statement.CleanupDiscriminantJoinBlockId.HasValue),
            "An uninitialized enum needs a validated guard before its variant payload cleanup.");
        SafeCoreMirFunction main = mir.Functions.Single(function => !function.IsDestructor);
        SafeCoreMirFunction forgedMain = new(main.Id, main.Name, main.ReturnType, main.Locals,
            main.Blocks.Select(block => new SafeCoreMirBlock(block.Id,
                block.Statements.Select(statement => statement.IsCleanupDiscriminant
                    ? statement with { CleanupDiscriminantJoinBlockId = main.EntryBlockId } : statement).ToArray(),
                block.Terminator, block.Source)).ToArray(), main.EntryBlockId, main.Source, main.IsPublic)
            { PanicStrategy = main.PanicStrategy };
        SafeCoreMirValidationResult rejected = SafeCoreMirValidation.Validate(new(
            mir.Functions.Select(function => function.Id == main.Id ? forgedMain : function).ToArray(), mir.AdtLayouts),
            new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 65_536 });
        AssertEx.False(rejected.IsSuccessful, "A cleanup marker may not redirect its region into ordinary body code.");
        await CompileAndRunAsync(uninitialized, "body\n").ConfigureAwait(false);
        const string moved = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            enum Choice { Owned(Marker), Empty }
            fn main() {
                let original = Choice::Owned(Marker);
                let moved = original;
                println!("body");
            }
            """;
        await CompileAndRunAsync(moved, "body\ndrop\n").ConfigureAwait(false);
    }

    private static Task CleanupSnapshotBindingAsync()
    {
        SafeCoreMirPipelineResult proof = Analyze(Source);
        AssertEx.True(proof.IsSuccessful, string.Join(Environment.NewLine, proof.Diagnostics));
        SafeCoreMirProgram original = proof.Mir!.Program!;
        SafeCoreMirProgram Alter(bool eraseDestructor, bool abort) => new(original.Functions.Select(function =>
            new SafeCoreMirFunction(function.Id, function.Name, function.ReturnType, function.Locals,
                function.Blocks, function.EntryBlockId, function.Source, function.IsPublic)
                { IsDestructor = eraseDestructor ? false : function.IsDestructor,
                    PanicStrategy = abort ? SafeCorePanicStrategy.Abort : function.PanicStrategy }).ToArray(), original.AdtLayouts);
        string Digest(SafeCoreMirProgram program) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(SafeCoreMirFormatting.Format(program,
                new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 65_536 }))));
        AssertEx.False(Digest(original) == Digest(Alter(true, false)),
            "An erased destructor identity must change the MIR snapshot evidence digest.");
        AssertEx.False(Digest(original) == Digest(Alter(false, true)),
            "Changing unwind to abort must change the MIR snapshot evidence digest.");
        return Task.CompletedTask;
    }

    private static Task EnumKnownFactsAsync()
    {
        const string source = """
            struct First;
            impl Drop for First { fn drop(&mut self) { println!("first"); } }
            struct Second;
            impl Drop for Second { fn drop(&mut self) { println!("second"); } }
            enum Choice { Left(First), Right(Second) }
            fn main() { let original = Choice::Left(First); let moved = original; }
            """;
        SafeCoreMirPipelineResult proof = Analyze(source);
        AssertEx.True(proof.IsSuccessful, string.Join(Environment.NewLine, proof.Diagnostics));
        SafeCoreOwnershipProgram evidence = proof.Ownership!.Program!;
        SafeCoreOwnershipFunction main = evidence.Functions[proof.Mir!.Program!.Functions.Single(function => !function.IsDestructor).Id];
        AssertEx.True(main.Blocks.SelectMany(block => block.Instructions).Any(instruction =>
            instruction.ConstantEnumDiscriminant == 0), "The constructor must publish its actual declared tag.");
        SafeCoreOwnershipPath[] paths = proof.Ownership.Ownership!.Paths.Where(path => path.FunctionName == main.Name).ToArray();
        AssertEx.True(paths.Length > 0 && paths.All(path => path.DropOrder.Length == 1 &&
            path.DropOrder[0].StartsWith("moved", StringComparison.Ordinal) &&
            path.DropOrder.SequenceEqual(paths[0].DropOrder)),
            "The selected live variant must be cleaned through its moved owner exactly once.");
        SafeCoreOwnershipProgram forged = new(evidence.Functions.Select(function => new SafeCoreOwnershipFunction(
            function.Name, function.Locals, function.Scopes,
            function.Blocks.Select(block => new SafeCoreOwnershipBlock(block.Id, block.ScopeId,
                block.Instructions.Select(instruction => instruction.ConstantEnumDiscriminant is null
                    ? instruction : instruction with { ConstantEnumDiscriminant = 1 }).ToArray(),
                block.Terminator, block.Source)).ToArray(), function.EntryBlockId,
            function.PanicStrategy, function.Source)).ToArray());
        SafeCoreMirOwnershipResult rejected = SafeCoreMirOwnershipAdapter.Analyze(proof.Mir!.Program!, forged,
            new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 131_072 });
        AssertEx.False(rejected.IsSuccessful, "Evidence may not replace a real constructor tag with another variant.");
        AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "Changed enum facts must report exact MIR/evidence drift.");
        SafeCoreOwnershipProgram alteredScope = new(evidence.Functions.Select(function => new SafeCoreOwnershipFunction(
            function.Name, function.Locals, function.Scopes,
            function.Blocks.Select(block => new SafeCoreOwnershipBlock(block.Id, block.ScopeId,
                block.Instructions.Select(instruction => instruction.ConstantEnumDiscriminant is null
                    ? instruction : instruction with { ScopeId = 0 }).ToArray(),
                block.Terminator, block.Source)).ToArray(), function.EntryBlockId,
            function.PanicStrategy, function.Source)).ToArray());
        SafeCoreMirOwnershipResult rejectedScope = SafeCoreMirOwnershipAdapter.Analyze(proof.Mir.Program!, alteredScope,
            new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 131_072 });
        AssertEx.False(rejectedScope.IsSuccessful, "Evidence may not change a canonical instruction's scope identity.");
        AssertEx.True(rejectedScope.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "Changed instruction scopes require an explicit evidence mismatch.");
        return Task.CompletedTask;
    }

    private static Task MutableKnownFactsAsync()
    {
        var source = new SafeCoreMirSource("p1-mutable-facts.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreType boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
        SafeCoreOwnershipLocal[] locals =
        [
            new(0, "owner", boolean, SafeCoreOwnershipKind.Copy, false, 0, false, false, source),
            new(1, "reference", SafeCoreType.Reference(boolean, true), SafeCoreOwnershipKind.Move, false, 0, true, false, source),
            new(2, "condition", boolean, SafeCoreOwnershipKind.Copy, false, 0, false, false, source),
        ];
        SafeCoreOwnershipBlock[] blocks =
        [
            new(0, 0,
                [SafeCoreOwnershipInstruction.Assign(0, source) with { ConstantBoolean = true },
                 SafeCoreOwnershipInstruction.Borrow(0, 1, true, source),
                 SafeCoreOwnershipInstruction.EndBorrow(1, source),
                 SafeCoreOwnershipInstruction.Assign(2, source) with { CopiedBooleanLocalId = 0 }],
                SafeCoreOwnershipTerminator.Branch(2, 1, 2, source), source),
            new(1, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(source), source),
            new(2, 0, [], SafeCoreOwnershipTerminator.Panic(source), source),
        ];
        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(new([
            new SafeCoreOwnershipFunction("mutable_facts", locals, [new SafeCoreOwnershipScope(0, -1, source)],
                blocks, 0, SafeCorePanicStrategy.Unwind, source)]),
            new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 16_384 });
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(2, result.Paths.Length, "A mutable owner loan must prevent reusing a stale exact boolean to prune the later branch.");
        return Task.CompletedTask;
    }

    private static Task CleanupGuardsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var valid = Method(
            [new ClrLirLoadLocal(1), new ClrLirLoadInt32(1), GuardCall("HasVariant", ClrLirType.Bool, ClrLirType.Any, ClrLirType.I32)],
            [new ClrLirLoadLocal(1), GuardCall("IsDropLive", ClrLirType.Bool, ClrLirType.Any)]);
        AssertEx.True(valid.Validate(deadline.Token).IsValid, "Pure tag and exact-place liveness guards must validate.");
        ClrLirInstruction[][] invalid =
        [
            [new ClrLirLoadLocal(1), new ClrLirStoreLocal(1), new ClrLirLoadLocal(0)],
            [new ClrLirLoadLocal(1)],
            [new ClrLirLoadLocal(0), GuardCall("IsDropLive", ClrLirType.Bool, ClrLirType.Bool)],
            [],
        ];
        for (int index = 0; index < invalid.Length; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(Method(invalid[index]).Validate(deadline.Token).Diagnostics.Any(diagnostic => diagnostic.Code == "LIR022"),
                $"Invalid cleanup guard {index} must fail before emission.");
        }
        return Task.CompletedTask;

        static ClrLirCall GuardCall(string name, ClrLirType result, params ClrLirType[] parameters) =>
            new(new("MirReference." + name, result, parameters)
            { ExternalCall = new("RustSharp.Runtime", "RustSharp.Runtime", "MirReference", name) });

        static ClrLirMethod Method(params ClrLirInstruction[][] guards) =>
            new("Main", ClrLirType.Void, [], [new("live", ClrLirType.Bool), new("owner", ClrLirType.Any)],
                [new("entry", [new ClrLirReturn()])], guardedExceptionCleanup:
                [new(new("Destructor", ClrLirType.Void, [ClrLirType.Any]), 0,
                    receiverLocalIndex: 1, guardInstructions: guards)]);
    }

    private static async Task BorrowedAliasGenerationsAsync()
    {
        const string prefix = """
            struct Marker { value: i32 }
            impl Drop for Marker { fn drop(&mut self) {
                println!("{}", self.value);
                if self.value == 1 { let max: i32 = 2147483647; println!("{}", max + 1); }
            } }
            fn make(value: i32) -> Marker { Marker { value: value } }
            enum Choice { One(Marker), Two(Marker, Marker), Empty }
            fn replace(value: &mut Choice) { *value = Choice::Two(make(2), make(3)); }
            fn main() {
                let retained = make(9);
                let mut choice = Choice::One(make(1));
                let alias = &mut choice;
                println!("body");
                replace(alias);
                println!("after");
            }
            """;
        await CompileAndRunAsync(prefix, "body\n1\n2\n3\n9\n", "OverflowException").ConfigureAwait(false);
        string normal = prefix.Replace("if self.value == 1 { let max: i32 = 2147483647; println!(\"{}\", max + 1); }", "", StringComparison.Ordinal);
        await CompileAndRunAsync(normal, "body\n1\nafter\n2\n3\n9\n").ConfigureAwait(false);
    }

    private static async Task CompileAndRunAsync(string programSource, string expected, string? failureFragment = null)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        const string prefix = "p1-drop-receiver-";
        string directory = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string outputPath = Path.Combine(directory, "program.dll");
            CompilationResult compiled = CompilerDriver.Compile(programSource, Path.Combine(directory, "program.rs"),
                outputPath, "P1DropReceiver", CompilationProfile.SafeCoreMirV2, compileDeadline.Token);
            AssertEx.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10), started =>
                    Console.WriteLine($"P1 Drop receiver process: pid={started.ProcessId} parent={started.ParentProcessId} start={started.StartedAt:O} command={started.CommandLine}")),
                runDeadline.Token).ConfigureAwait(false);
            if (failureFragment is null) AssertEx.True(run.Succeeded, run.StandardError);
            else
            {
                AssertEx.False(run.Succeeded, "The generated alias replacement must propagate its expected destructor failure.");
                AssertEx.True(run.StandardError.Contains(failureFragment, StringComparison.Ordinal), run.StandardError);
            }
            AssertEx.False(run.ProcessTreeCleanupIncomplete, run.ProcessTreeCleanupDiagnostic ?? "Receiver process tree cleanup must complete.");
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            await DeleteOwnedDirectoryAsync(directory, root, prefix).ConfigureAwait(false);
        }
    }

    private static async Task DeleteOwnedDirectoryAsync(string directory, string root, string prefix)
    {
        string fullPath = Path.GetFullPath(directory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(fullPath), root, comparison) ||
            !Path.GetFileName(fullPath).StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(fullPath).AsSpan(prefix.Length), "N", out _))
            throw new InvalidOperationException("Refusing to clean an unowned receiver directory.");
        if (!Directory.Exists(fullPath)) return;
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to clean a redirected receiver directory.");
        var clock = Stopwatch.StartNew();
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { Directory.Delete(fullPath, recursive: true); }
            catch (IOException exception) { lastFailure = exception; }
            catch (UnauthorizedAccessException exception) { lastFailure = exception; }
            if (!Directory.Exists(fullPath)) return;
            TimeSpan remaining = TimeSpan.FromSeconds(5) - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(150, remaining.TotalMilliseconds))).ConfigureAwait(false);
        }
        throw new IOException("Receiver directory cleanup exceeded forty attempts or five seconds.", lastFailure);
    }

    private static SafeCoreMirPipelineResult Analyze(string source, CancellationToken cancellationToken = default) =>
        SafeCoreMirPipeline.Analyze(source, "p1-drop-receiver.rs", new()
        {
            EnableP1Extensions = true,
            RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(10),
            CancellationToken = cancellationToken,
        });
}
