using System.Collections.Immutable;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P1ImportedAggregateTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-09 imported source nominal types retain constructor shape and enum tags", ImportedConstructorsAsync),
        new("P1-09 imported nested source modules preserve function and type paths", NestedExportsAsync),
        new("P1-09 verified re-exported constructors keep their original nominal owner", ReexportedConstructorsAsync),
        new("P1-09 imported constructors and fields enforce producer privacy", PrivateFieldsAsync),
        new("P1-09 private producer type names reject matching consumer module paths", PrivateTypesAsync),
        new("P1-09 consumer nominal declarations cannot impersonate matching foreign owner paths", NominalScopeCollisionAsync),
        new("P1-09 imported enum evidence rejects invented offsets and tags", InvalidEnumLayoutsAsync),
        new("P1-09 anonymous source shape rejects multiple producer CLR owners", ConflictingOwnersAsync),
        new("P1-09 returned enum variants follow overwrite branches nested values and calls", ReturnedVariantsAsync),
        new("P1-09 returned enum variants preserve unknown input and execution budgets", ReturnedVariantLimitsAsync),
    ];

    private static readonly SafeCoreMirSource Source = new("returned-variants.rs", new(0, 1), 0, 1);
    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
    private static readonly SafeCoreType EnumType = SafeCoreType.Adt("crate::producer::Choice");

    private static SafeCoreExternalType Choice() => new("crate::Choice", new("crate::Choice", "Choice",
        [new("$tag", "i32", false), new("$v1$0", "i32", true), new("$v2$value", "i32", true)], false, "Producer")
        {
            Kind = SafeCoreExternalTypeKind.Enum,
            Variants =
            [
                new("None", -3, 1, []) { Kind = SafeCoreExternalTypeKind.UnitStruct },
                new("Some", 7, 1, [new("0", "i32", true)]) { Kind = SafeCoreExternalTypeKind.TupleStruct },
                new("Named", 8, 2, [new("value", "i32", true)]) { Kind = SafeCoreExternalTypeKind.NamedStruct },
            ],
        }, "producer.dll", "crate::producer") { Kind = SafeCoreExternalTypeKind.Enum };

    private static SafeCoreExternalType Record(string name, SafeCoreExternalTypeKind kind, bool fieldPublic = true) =>
        new("crate::" + name, new("crate::" + name, name,
            kind == SafeCoreExternalTypeKind.UnitStruct ? [] : [new(kind == SafeCoreExternalTypeKind.TupleStruct ? "0" : "value", "i32", fieldPublic)],
            false, "Producer") { Kind = kind }, "producer.dll", "crate::producer") { Kind = kind };

    private static SafeCoreMirPipelineResult Check(string source, params SafeCoreExternalType[] types)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return SafeCoreMirPipeline.Analyze(source, "imported-aggregates.rs", new()
        {
            EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5), CancellationToken = timeout.Token,
            Crates =
            [
                new("crate", "consumer", ImmutableDictionary<string, string>.Empty.Add("package", "crate::producer")),
                new("crate::producer", "producer", ImmutableDictionary<string, string>.Empty) { TypeExports = types.ToImmutableArray() },
            ],
        });
    }

    private static Task ImportedConstructorsAsync()
    {
        const string source = """
            use package::Choice::Some as Wrapped;
            fn main() {
                let point: package::Point = package::Point { value: 1 };
                let pair: package::Pair = package::Pair(2);
                let unit: package::Unit = package::Unit;
                let selected: package::Choice = Wrapped(4);
                let named = package::Choice::Named { value: 5 };
                let empty = package::Choice::None;
                let result = match selected {
                    package::Choice::None => 0,
                    package::Choice::Some(value) => value,
                    package::Choice::Named { value } => value,
                };
                println!("{}", point.value + pair.0 + result);
            }
            """;
        SafeCoreMirPipelineResult result = Check(source, Record("Point", SafeCoreExternalTypeKind.NamedStruct),
            Record("Pair", SafeCoreExternalTypeKind.TupleStruct), Record("Unit", SafeCoreExternalTypeKind.UnitStruct), Choice());
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        SafeCoreMirProgram mir = result.Mir!.Program!;
        SafeCoreMirAdtLayout choice = mir.AdtLayouts.Single(layout => layout.Type == EnumType);
        AssertEx.Equal(-3, choice.Variants[0].Discriminant);
        AssertEx.Equal(7, choice.Variants[1].Discriminant);
        AssertEx.Equal(2, choice.Variants[2].FieldOffset);
        AssertEx.True(mir.Functions.Single().Locals.Any(local => local.Type.Name == "crate::producer::Unit" && local.IsUnitAdt),
            "Imported unit constructors must retain declaration evidence in MIR.");
        AssertEx.Equal(0, mir.ExternalFunctions.Count);
        AssertEx.True(mir.Functions.Single().Blocks.SelectMany(block => block.Statements).Any(statement =>
            statement.Value.Kind == SafeCoreMirRvalueKind.Enum), "Imported enum constructors must emit actual enum values.");
        return Task.CompletedTask;
    }

    private static Task PrivateFieldsAsync()
    {
        SafeCoreExternalType named = Record("Private", SafeCoreExternalTypeKind.NamedStruct, fieldPublic: false);
        SafeCoreExternalType tuple = Record("Tuple", SafeCoreExternalTypeKind.TupleStruct, fieldPublic: false);
        string[] sources =
        [
            "fn main() { let value = package::Private { value: 1 }; }",
            "fn main() { let value = package::Tuple(1); }",
            "fn field(value: package::Private) -> i32 { value.value } fn main() {}",
            "fn field(value: package::Tuple) -> i32 { value.0 } fn main() {}",
            "fn field(value: package::Private) { let package::Private { value } = value; } fn main() {}",
            "mod producer { pub fn field() { let value = package::Private { value: 1 }; } } fn main() {}",
            "mod producer { pub fn field() { let value = package::Tuple(1); } } fn main() {}",
            "mod producer { pub fn field(value: package::Private) { let package::Private { value } = value; } } fn main() {}",
        ];
        foreach (string source in sources)
        {
            SafeCoreMirPipelineResult result = Check(source, named, tuple);
            AssertEx.False(result.IsSuccessful, "Producer-private fields must reject construction, access and destructuring.");
            AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "RST2002"), Format(result.Diagnostics));
            AssertEx.True(result.Mir?.Program is null, "A type privacy failure must not publish MIR evidence.");
        }
        return Task.CompletedTask;
    }

    private static Task NestedExportsAsync()
    {
        SafeCoreExternalType point = Record("Point", SafeCoreExternalTypeKind.NamedStruct);
        point = point with { SourceQualifiedName = "crate::nested::Point", Layout = point.Layout with { Name = "crate::nested::Point" } };
        var add = new SafeCoreExternalFunction("crate::nested::add", "add", "I32->I32", "Producer", "producer.dll")
        {
            SourceSchema = "rustsharp-source-call-v1", SourceParameterTypes = ["i32"], SourceReturnType = "i32",
            SourceParameterStaticLifetimes = [false], CallPanicStrategy = "unwind", CallParameterContracts = ["copy"],
            CallReturnContract = "copy", NominalScope = "crate::producer",
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(
            "use package::nested::Point as Record; fn main() { let point: Record = Record { value: 4 }; println!(\"{}\", package::nested::add(point.value)); }",
            "nested-imports.rs", new()
            {
                EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(5), CancellationToken = timeout.Token,
                Crates =
                [
                    new("crate", "consumer", ImmutableDictionary<string, string>.Empty.Add("package", "crate::producer")),
                    new("crate::producer", "producer", ImmutableDictionary<string, string>.Empty) { TypeExports = [point], Exports = [add] },
                ],
            });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        AssertEx.Equal("crate::producer::nested::Point", result.Mir!.Program!.AdtLayouts.Single().Type.Name!);
        AssertEx.Equal("crate::nested::add", result.Mir.Program.ExternalFunctions.Single().ExternalFunction.SourceQualifiedName);
        return Task.CompletedTask;
    }

    private static Task PrivateTypesAsync()
    {
        SafeCoreExternalType hidden = Record("Hidden", SafeCoreExternalTypeKind.NamedStruct) with { IsPublic = false };
        SafeCoreExternalType tuple = Record("HiddenTuple", SafeCoreExternalTypeKind.TupleStruct) with { IsPublic = false };
        SafeCoreExternalType choice = Choice() with { IsPublic = false };
        (string Source, string PrivatePath, bool ValuePath)[] cases =
        [
            ("mod producer { pub fn forge() { let value = package::Hidden { value: 1 }; } } fn main() {}", "package::Hidden", true),
            ("mod producer { pub fn forge() { let value = package::HiddenTuple(1); } } fn main() {}", "package::HiddenTuple", true),
            ("mod producer { pub fn forge(value: package::Hidden) {} } fn main() {}", "package::Hidden", false),
            ("mod producer { pub fn forge() { let value = package::Choice::None; } } fn main() {}", "package::Choice::None", true),
            ("mod producer { pub fn forge() { let package::Hidden { value: n } = package::make_hidden(); } } fn main() {}", "package::Hidden", true),
            ("mod producer { pub fn forge() { let package::HiddenTuple(n) = package::make_hidden_tuple(); } } fn main() {}", "package::HiddenTuple", true),
            ("mod producer { pub fn forge() { let n = match package::make_choice() { package::Choice::None => 0, package::Choice::Some(n) => n, package::Choice::Named { value: n } => n }; } } fn main() {}", "package::Choice::None", true),
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        foreach ((string source, string privatePath, bool valuePath) in cases)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreMirPipelineResult visible = CheckPrivateTypes(source, [hidden with { IsPublic = true }, tuple with { IsPublic = true }, choice with { IsPublic = true }]);
            AssertEx.True(visible.IsSuccessful, "The identical source must succeed when the selected producer names are public: " + Format(visible.Diagnostics));
            SafeCoreMirPipelineResult result = CheckPrivateTypes(source, [hidden, tuple, choice]);
            AssertEx.False(result.IsSuccessful, "Matching a module name cannot authorize another producer's private source type.");
            Diagnostic? denial = result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Message.Contains("'" + privatePath + "'", StringComparison.Ordinal) &&
                (diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.PrivateName ||
                    valuePath && diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnresolvedName));
            AssertEx.True(denial is not null, "The resolver must reject the exact private type/constructor/pattern path: " + Format(result.Diagnostics));
            // A private value constructor may fail the value namespace lookup
            // before the semantic visibility guard. Type annotations require
            // the resolver's explicit private-name branch.
            if (!valuePath) AssertEx.Equal(SafeCoreNameResolutionDiagnosticCodes.PrivateName, denial!.Code);
            AssertEx.True(result.Mir?.Program is null, "Private producer type names must reject before MIR publication.");
        }
        return Task.CompletedTask;

        SafeCoreMirPipelineResult CheckPrivateTypes(string source, SafeCoreExternalType[] types)
        {
            SafeCoreExternalFunction[] makers = types.Select((type, index) => new SafeCoreExternalFunction(
                "crate::" + (index == 0 ? "make_hidden" : index == 1 ? "make_hidden_tuple" : "make_choice"),
                "make_" + index, "->Value(" + type.Layout.ClrName + ")", "Producer", "producer.dll")
            {
                SourceSchema = "rustsharp-source-call-v1", SourceReturnType = type.Layout.Name, SourceValueTypes = [type.Layout],
                SourceReturnVariants = type.Kind == SafeCoreExternalTypeKind.Enum
                    ? type.Layout.Variants.Select(variant => new SafeCoreExternalReturnedVariant([], variant.Name)).ToImmutableArray()
                    : [],
                CallPanicStrategy = "unwind", CallReturnContract = "move", NominalScope = "crate::producer",
            }).ToArray();
            return SafeCoreMirPipeline.Analyze(source, "private-imported-types.rs", new()
            {
                EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token,
                Crates =
                [
                    new("crate", "consumer", ImmutableDictionary<string, string>.Empty.Add("package", "crate::producer")),
                    new("crate::producer", "producer", ImmutableDictionary<string, string>.Empty) { TypeExports = types.ToImmutableArray(), Exports = makers.ToImmutableArray() },
                ],
            });
        }
    }

    private static Task InvalidEnumLayoutsAsync()
    {
        SafeCoreExternalType original = Choice();
        SafeCoreExternalVariant some = original.Layout.Variants[1];
        SafeCoreExternalType[] invalid =
        [
            original with { Layout = original.Layout with { Variants = original.Layout.Variants.SetItem(1, some with { FieldOffset = 2 }) } },
            original with { Layout = original.Layout with { Variants = original.Layout.Variants.SetItem(1, some with { Discriminant = -3 }) } },
            original with { Layout = original.Layout with { Fields = original.Layout.Fields.SetItem(1, new("$v1$other", "i32", true)) } },
        ];
        foreach (SafeCoreExternalType descriptor in invalid)
        {
            SafeCoreMirPipelineResult result = Check("fn main() {}", descriptor);
            AssertEx.False(result.IsSuccessful, "Imported enum identities require matching tag and physical payload evidence.");
            AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "RST2002"), Format(result.Diagnostics));
        }
        return Task.CompletedTask;
    }

    private static Task NominalScopeCollisionAsync()
    {
        SafeCoreExternalType exported = Record("Hidden", SafeCoreExternalTypeKind.NamedStruct);
        SafeCoreMirPipelineResult distinct = Check("struct Hidden { value: bool } fn main() { " +
            "let local = Hidden { value: true }; let foreign = package::Hidden { value: 42 }; " +
            "println!(\"{}\", local.value); println!(\"{}\", foreign.value); }", exported);
        AssertEx.True(distinct.IsSuccessful, "Local and producer names must coexist with different layouts and distinct owners: " + Format(distinct.Diagnostics));
        AssertEx.True(distinct.Mir!.Program!.AdtLayouts.Count == 2 && distinct.Mir.Program.AdtLayouts.Select(layout => layout.Type.Name)
            .Distinct(StringComparer.Ordinal).Count() == 2, "Identically spelled type names from different scopes must retain two actual nominal layouts.");
        string[] declarations =
        [
            "mod producer { pub struct Hidden { pub value: bool } } fn main() {}",
            "mod producer { pub struct Hidden { pub value: i32 } } fn main() {}",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (string source in declarations)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreMirPipelineResult collision = Check(source, exported);
            AssertEx.False(collision.IsSuccessful, "A local declaration cannot obtain a foreign owner by matching its full nominal path or layout.");
            AssertEx.True(collision.Diagnostics.Any(diagnostic => diagnostic.Code == "RST2002" &&
                diagnostic.Message.Contains("collides with a verified foreign type owner", StringComparison.Ordinal)), Format(collision.Diagnostics));
            AssertEx.True(collision.Mir?.Program is null, "A nominal owner collision must reject before MIR publication.");
        }
        return Task.CompletedTask;
    }

    private static Task ReexportedConstructorsAsync()
    {
        SafeCoreExternalType[] originals = [Record("Point", SafeCoreExternalTypeKind.NamedStruct),
            Record("Pair", SafeCoreExternalTypeKind.TupleStruct), Record("Unit", SafeCoreExternalTypeKind.UnitStruct), Choice()];
        SafeCoreExternalType[] aliases = originals.Select(original => original with
        {
            SourceQualifiedName = "crate::Alias" + original.SourceName, NominalScope = "crate::facade", AssemblyPath = "facade.dll",
            Layout = original.Layout with
            {
                Name = "crate::Alias" + original.SourceName, NominalScope = "crate::producer", NominalSourceName = original.Layout.Name,
                Owner = new("Producer", original.Layout.Name, original.Layout.ClrName,
                    new("11111111-2222-3333-4444-555555555555"), new string('a', 64), new string('b', 64)),
            },
        }).ToArray();
        const string source = """
            fn main() {
                let point: base::Point = facade::AliasPoint { value: 3 };
                let pair: base::Pair = facade::AliasPair(4);
                let unit: base::Unit = facade::AliasUnit;
                let selected: base::Choice = facade::AliasChoice::Some(5);
                let value = match selected {
                    base::Choice::None => 0,
                    base::Choice::Some(value) => value,
                    base::Choice::Named { value } => value,
                };
                println!("{}", point.value + pair.0 + value);
            }
            """;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(source, "verified-reexports.rs", new()
        {
            EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5), CancellationToken = timeout.Token,
            Crates =
            [
                new("crate", "consumer", ImmutableDictionary<string, string>.Empty.Add("base", "crate::producer").Add("facade", "crate::facade")),
                new("crate::producer", "producer", ImmutableDictionary<string, string>.Empty) { TypeExports = originals.ToImmutableArray() },
                new("crate::facade", "facade", ImmutableDictionary<string, string>.Empty) { TypeExports = aliases.ToImmutableArray() },
            ],
        });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        AssertEx.Equal(4, result.Mir!.Program!.AdtLayouts.Count);
        AssertEx.True(result.Mir.Program.AdtLayouts.All(layout => layout.Type.Name!.StartsWith("crate::producer::", StringComparison.Ordinal)),
            "A source alias must retain its producer's nominal type and CLR ownership.");
        return Task.CompletedTask;
    }

    private static Task ConflictingOwnersAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze("fn main() {}", "structural-conflict.rs", new()
        {
            EnableP1Extensions = true, Timeout = TimeSpan.FromSeconds(5), CancellationToken = timeout.Token,
            Crates =
            [
                new("crate", "consumer", ImmutableDictionary<string, string>.Empty.Add("package", "crate::producer")),
                new("crate::producer", "producer", ImmutableDictionary<string, string>.Empty)
                {
                    StructuralTypes = [new("(i32, bool)", "Producer", "TupleA", "crate::producer"), new("(i32, bool)", "Other", "TupleB", "crate::other")],
                },
            ],
        });
        AssertEx.False(result.IsSuccessful, "Anonymous aggregate CLR ownership conflicts require a checked conversion.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "RST2002"), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static SafeCoreMirAdtLayout Layout() => new(EnumType,
        [new("$tag", Integer, Source), new("$v1$0", Integer, Source)],
        [new("crate::producer::Choice::None", -3, 1, [], Source), new("crate::producer::Choice::Some", 7, 1, [new("0", Integer, Source)], Source)], Source);

    private static SafeCoreMirLocal Local(int id, SafeCoreType type, bool parameter = false) =>
        new(id, "local" + id, type, parameter ? SafeCoreMirLocalKind.Parameter : SafeCoreMirLocalKind.Temporary, true, Source);
    private static SafeCoreMirOperand Operand(int id, SafeCoreType type) => SafeCoreMirOperand.Local(id, type, Source);
    private static SafeCoreMirStatement Construct(int destination, int variant) => new(destination,
        SafeCoreMirRvalue.Enum(variant, variant == 0 ? [] : [SafeCoreMirOperand.Constant(Integer, "1", Source)], EnumType, Source), Source);
    private static SafeCoreMirBlock Block(int id, IReadOnlyList<SafeCoreMirStatement> statements, SafeCoreMirTerminator terminator) => new(id, statements, terminator, Source);

    private static Task ReturnedVariantsAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([EnumType, EnumType]);
        SafeCoreMirFunction none = new(0, "none", EnumType, [Local(0, EnumType)],
            [Block(0, [Construct(0, 1), Construct(0, 0)], SafeCoreMirTerminator.Return(Operand(0, EnumType), Source))], 0, Source);
        SafeCoreMirFunction choice = new(1, "choice", EnumType, [Local(0, Boolean, true), Local(1, EnumType)],
            [Block(0, [], SafeCoreMirTerminator.Branch(Operand(0, Boolean), 1, 2, Source)),
             Block(1, [Construct(1, 0)], SafeCoreMirTerminator.Goto(3, Source)),
             Block(2, [Construct(1, 1)], SafeCoreMirTerminator.Goto(3, Source)),
             Block(3, [], SafeCoreMirTerminator.Return(Operand(1, EnumType), Source))], 0, Source);
        SafeCoreMirFunction nested = new(2, "nested", tuple, [Local(0, EnumType), Local(1, EnumType), Local(2, tuple)],
            [Block(0, [Construct(1, 1)], SafeCoreMirTerminator.Call(SafeCoreMirOperand.Function(0,
                SafeCoreType.Function([], EnumType, "none"), Source), [], 0, 1, Source)),
             Block(1, [new(2, SafeCoreMirRvalue.Tuple([Operand(0, EnumType), Operand(1, EnumType)], tuple, Source), Source)],
                SafeCoreMirTerminator.Return(Operand(2, tuple), Source))], 0, Source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreMirReturnedVariantsResult result = SafeCoreMirReturnedVariants.Analyze(new([none, choice, nested], [Layout()]),
            new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = timeout.Token });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        AssertEx.Equal("crate::producer::Choice::None", result.Functions[0].Variants.Single().VariantNames.Single());
        AssertEx.Equal(2, result.Functions[1].Variants.Single().VariantNames.Count);
        AssertEx.Equal(2, result.Functions[2].Variants.Count);
        AssertEx.Equal("crate::producer::Choice::None", result.Functions[2].Variants.Single(variant => variant.ValuePath[0].Index == 0).VariantNames.Single());
        AssertEx.Equal("crate::producer::Choice::Some", result.Functions[2].Variants.Single(variant => variant.ValuePath[0].Index == 1).VariantNames.Single());
        return Task.CompletedTask;
    }

    private static Task ReturnedVariantLimitsAsync()
    {
        SafeCoreMirFunction relay = new(0, "relay", EnumType, [Local(0, EnumType, true)],
            [Block(0, [], SafeCoreMirTerminator.Return(Operand(0, EnumType), Source))], 0, Source);
        SafeCoreMirProgram program = new([relay], [Layout()]);
        SafeCoreMirReturnedVariantsResult unknown = SafeCoreMirReturnedVariants.Analyze(program);
        AssertEx.True(unknown.IsSuccessful, Format(unknown.Diagnostics));
        AssertEx.Equal(2, unknown.Functions.Single().Variants.Single().VariantNames.Count);
        SafeCoreMirReturnedVariantsResult limited = SafeCoreMirReturnedVariants.Analyze(program, new() { MaximumOperations = 1 });
        AssertEx.False(limited.IsSuccessful, "Exhausted budgets must not publish empty successful enum facts.");
        AssertEx.True(limited.IsTruncated && limited.Functions.Count == 0, "An exhausted budget cannot publish successful partial facts.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirReturnedVariants.Analyze(program, new() { CancellationToken = cancelled.Token }));
        return Task.CompletedTask;
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message));
}
