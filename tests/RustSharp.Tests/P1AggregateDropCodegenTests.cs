using System.Diagnostics;
using RustSharp.Compiler;
using RustSharp.Runtime;

namespace RustSharp.Tests;

/// <summary>Source-to-generated-PE evidence for recursive aggregate destruction.</summary>
internal static class P1AggregateDropCodegenTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 generated array Drop visits every element in order", ArrayElementsAsync),
        new("P1 generated tuple and nested struct Drop preserve field order", NestedTupleAsync),
        new("P1 generated enum Drop visits only the active variant", ActiveEnumAsync),
        new("P1 generated array Drop unwinds every initialized element", ArrayFaultAsync),
        new("P1 generated array Drop does not repeat completed field cleanup on fault", CompletedArrayThenFaultAsync),
        new("P1 generated aggregate Drop skips a moved field", PartialMoveAsync),
        new("P1 generated aggregate Drop unwinds the moved value and remaining field once", PartialMoveFaultAsync),
        new("P1 generated enum Drop unwinds only the active payload", ActiveEnumFaultAsync),
        new("P1 generated aggregate construction panic drops only initialized operand temporaries", PartialConstructionFaultAsync),
        new("P1 generated aggregate assignment destroys replaced fields before storing RHS", AggregateReplacementAsync),
        new("P1 generated projected assignment destroys only the replaced field", FieldReplacementAsync),
        new("P1 generated assignment RHS panic retains the old owner", ReplacementRhsFaultAsync),
        new("P1 generated expression temporary drops at statement end", StatementTemporaryAsync),
        new("P1 generated call argument temporary drops after its borrow is consumed", BorrowedArgumentTemporaryAsync),
        new("P1 generated direct temporary borrow extends to the owning let scope", ExtendedBorrowTemporaryAsync),
        new("P1 generated wildcard rvalue drops at statement end", WildcardTemporaryAsync),
        new("P1 generated block tail transfers temporary ownership before cleanup", BlockTailTemporaryAsync),
        new("P1 generated owner Drop consumes fields after projected replacement and panic", OwningFieldReplacementFaultAsync),
        new("P1 generated replacement destructor panic cleans evaluated RHS and stops the statement", ReplacementDropFaultAsync),
        new("P1 generated partial struct construction cleans a completed zero size Drop operand", UnitStructConstructionFaultAsync),
        new("P1 generated partial tuple construction cleans a completed zero size Drop operand", UnitTupleConstructionFaultAsync),
        new("P1 generated partial array construction cleans a completed zero size Drop operand", UnitArrayConstructionFaultAsync),
        new("P1 generated partial enum construction cleans a completed zero size Drop operand", UnitEnumConstructionFaultAsync),
        new("P1 generated later argument panic cleans a completed zero size Drop argument", UnitArgumentFaultAsync),
        new("P1 generated nested normal cleanup retains owner failure and continues after field failure", NestedNormalFailuresAsync),
        new("P1 generated nested unwind stops after the owner failure before visiting fields", NestedUnwindOwnerFailureAsync),
        new("P1 generated mutable enum replacement drops old and current payloads once", MutableEnumCallReplacementAsync),
        new("P1 generated mutable enum replacement unwinds the current payload after callee panic", MutableEnumCallReplacementFaultAsync),
        new("P1 generated borrowed enum replacement destructor panic shares consumed state with its caller", MutableEnumReplacementDropFaultAsync),
    ];

    private static async Task ArrayElementsAsync()
    {
        const string source = """
            struct Marker { value: i32 }
            impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
            fn main() {
                let empty: [Marker; 0] = [];
                let values: [Marker; 3] = [Marker { value: 1 }, Marker { value: 2 }, Marker { value: 3 }];
                println!("body");
            }
            """;
        await CompileAndRunAsync(source, "body\n1\n2\n3\n").ConfigureAwait(false);
    }

    private static async Task NestedTupleAsync()
    {
        const string source = """
            struct First;
            impl Drop for First { fn drop(&mut self) { println!("first"); } }
            struct Second;
            impl Drop for Second { fn drop(&mut self) { println!("second"); } }
            struct Fields { pair: (First, Second), tail: First }
            fn main() { let fields = Fields { pair: (First, Second), tail: First }; println!("body"); }
            """;
        await CompileAndRunAsync(source, "body\nfirst\nsecond\nfirst\n").ConfigureAwait(false);
    }

    private static async Task ActiveEnumAsync()
    {
        const string source = """
            struct First;
            impl Drop for First { fn drop(&mut self) { println!("first"); } }
            struct Second;
            impl Drop for Second { fn drop(&mut self) { println!("second"); } }
            enum Choice { One(First), Two(Second, First), Empty }
            fn main() {
                { let one = Choice::One(First); println!("one"); }
                { let two = Choice::Two(Second, First); println!("two"); }
                { let empty = Choice::Empty; println!("empty"); }
            }
            """;
        await CompileAndRunAsync(source, "one\nfirst\ntwo\nsecond\nfirst\nempty\n").ConfigureAwait(false);
    }

    private static async Task ArrayFaultAsync()
    {
        const string source = """
            struct Marker { value: i32 }
            impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
            fn overflow(value: i32) -> i32 { value + 1 }
            fn main() {
                let empty: [Marker; 0] = [];
                let values: [Marker; 3] = [Marker { value: 1 }, Marker { value: 2 }, Marker { value: 3 }];
                println!("body");
                let max: i32 = 2147483647;
                println!("{}", overflow(max));
            }
            """;
        await CompileAndRunAsync(source, "body\n1\n2\n3\n", expectedSuccess: false).ConfigureAwait(false);
    }

    private static async Task CompletedArrayThenFaultAsync()
    {
        const string source = """
            struct Marker { value: i32 }
            impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
            fn overflow(value: i32) -> i32 { value + 1 }
            fn main() {
                { let values: [Marker; 2] = [Marker { value: 1 }, Marker { value: 2 }]; println!("scope"); }
                println!("after");
                let max: i32 = 2147483647;
                println!("{}", overflow(max));
            }
            """;
        await CompileAndRunAsync(source, "scope\n1\n2\nafter\n", expectedSuccess: false).ConfigureAwait(false);
    }

    private static async Task PartialMoveAsync()
    {
        const string source = """
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
        await CompileAndRunAsync(source, "body\nfirst\nsecond\n").ConfigureAwait(false);
    }

    private static async Task ActiveEnumFaultAsync()
    {
        const string source = """
            struct First;
            impl Drop for First { fn drop(&mut self) { println!("first"); } }
            struct Second;
            impl Drop for Second { fn drop(&mut self) { println!("second"); } }
            enum Choice { One(First), Two(Second, First), Empty }
            fn overflow(value: i32) -> i32 { value + 1 }
            fn main() {
                let choice = Choice::Two(Second, First);
                println!("body");
                let max: i32 = 2147483647;
                println!("{}", overflow(max));
            }
            """;
        await CompileAndRunAsync(source, "body\nsecond\nfirst\n", expectedSuccess: false).ConfigureAwait(false);
    }

    private static async Task PartialMoveFaultAsync()
    {
        const string source = """
            struct First;
            impl Drop for First { fn drop(&mut self) { println!("first"); } }
            struct Second;
            impl Drop for Second { fn drop(&mut self) { println!("second"); } }
            struct Fields { first: First, second: Second }
            fn overflow(value: i32) -> i32 { value + 1 }
            fn main() {
                let fields = Fields { first: First, second: Second };
                let moved = fields.first;
                println!("body");
                let max: i32 = 2147483647;
                println!("{}", overflow(max));
            }
            """;
        await CompileAndRunAsync(source, "body\nfirst\nsecond\n", expectedSuccess: false).ConfigureAwait(false);
    }

    private static async Task PartialConstructionFaultAsync()
    {
        const string source = """
            struct Marker { value: i32 }
            impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
            struct Fields { first: Marker, second: Marker }
            fn make(value: i32) -> Marker { Marker { value: value + 1 } }
            fn main() {
                let fields = Fields { first: Marker { value: 1 }, second: make(2147483647) };
                println!("later");
            }
            """;
        await CompileAndRunAsync(source, "1\n", expectedSuccess: false).ConfigureAwait(false);
    }

    private const string MarkerPrelude = """
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        fn make(value: i32) -> Marker { Marker { value: value } }

        """;

    private static Task AggregateReplacementAsync() => CompileAndRunAsync(MarkerPrelude + """
        struct Fields { first: Marker, second: Marker }
        fn main() {
            let mut fields = Fields { first: make(1), second: make(2) };
            fields = Fields { first: make(3), second: make(4) };
            println!("body");
        }
        """, "1\n2\nbody\n3\n4\n");

    private static Task FieldReplacementAsync() => CompileAndRunAsync(MarkerPrelude + """
        struct Fields { first: Marker, second: Marker }
        fn main() {
            let mut fields = Fields { first: make(1), second: make(2) };
            fields.first = make(3);
            println!("body");
        }
        """, "1\nbody\n3\n2\n");

    private static Task ReplacementRhsFaultAsync() => CompileAndRunAsync(MarkerPrelude + """
        fn overflow(value: i32) -> i32 { value + 1 }
        fn main() {
            let mut value = make(1);
            value = make(overflow(2147483647));
            println!("later");
        }
        """, "1\n", expectedSuccess: false);

    private static Task StatementTemporaryAsync() => CompileAndRunAsync(MarkerPrelude + """
        fn main() { make(1); println!("after"); }
        """, "1\nafter\n");

    private static Task BorrowedArgumentTemporaryAsync() => CompileAndRunAsync(MarkerPrelude + """
        fn observe(value: &Marker) { println!("{}", value.value); }
        fn main() { observe(&make(1)); println!("after"); }
        """, "1\n1\nafter\n");

    private static Task ExtendedBorrowTemporaryAsync() => CompileAndRunAsync(MarkerPrelude + """
        fn main() { let view = &make(1); println!("{}", view.value); println!("body"); }
        """, "1\nbody\n1\n");

    private static Task WildcardTemporaryAsync() => CompileAndRunAsync(MarkerPrelude + """
        fn main() { let _ = make(1); println!("after"); }
        """, "1\nafter\n");

    private static Task BlockTailTemporaryAsync() => CompileAndRunAsync(MarkerPrelude + """
        fn main() { let value = { make(1) }; println!("body"); }
        """, "body\n1\n");

    private static Task OwningFieldReplacementFaultAsync() => CompileAndRunAsync(MarkerPrelude + """
        struct Fields { first: Marker, second: Marker }
        impl Drop for Fields { fn drop(&mut self) { println!("owner"); } }
        fn overflow(value: i32) -> i32 { value + 1 }
        fn main() {
            let mut fields = Fields { first: make(1), second: make(2) };
            fields.second = make(3);
            println!("body");
            println!("{}", overflow(2147483647));
        }
        """, "2\nbody\nowner\n1\n3\n", expectedSuccess: false);

    private static Task ReplacementDropFaultAsync() => CompileAndRunAsync("""
        struct Marker { value: i32 }
        fn overflow(value: i32) -> i32 { value + 1 }
        impl Drop for Marker {
            fn drop(&mut self) {
                println!("{}", self.value);
                if self.value == 1 { println!("{}", overflow(2147483647)); }
            }
        }
        fn make(value: i32) -> Marker { Marker { value: value } }
        fn main() { let mut value = make(1); value = make(2); println!("unreachable"); }
        """, "1\n2\n", expectedSuccess: false);

    private const string UnitConstructionPrelude = """
        struct First;
        impl Drop for First { fn drop(&mut self) { println!("first"); } }
        fn overflow(value: i32) -> i32 { value + 1 }
        fn fail(value: i32) -> First { println!("{}", overflow(value)); First }

        """;

    private static Task UnitStructConstructionFaultAsync() => CompileAndRunAsync(UnitConstructionPrelude + """
        struct Fields { first: First, second: First }
        fn main() { let fields = Fields { first: First, second: fail(2147483647) }; println!("unreachable"); }
        """, "first\n", expectedSuccess: false);

    private static Task UnitTupleConstructionFaultAsync() => CompileAndRunAsync(UnitConstructionPrelude + """
        fn main() { let pair = (First, fail(2147483647)); println!("unreachable"); }
        """, "first\n", expectedSuccess: false);

    private static Task UnitArrayConstructionFaultAsync() => CompileAndRunAsync(UnitConstructionPrelude + """
        fn main() { let values: [First; 2] = [First, fail(2147483647)]; println!("unreachable"); }
        """, "first\n", expectedSuccess: false);

    private static Task UnitEnumConstructionFaultAsync() => CompileAndRunAsync(UnitConstructionPrelude + """
        enum Choice { Both(First, First) }
        fn main() { let value = Choice::Both(First, fail(2147483647)); println!("unreachable"); }
        """, "first\n", expectedSuccess: false);

    private static Task UnitArgumentFaultAsync() => CompileAndRunAsync(UnitConstructionPrelude + """
        fn consume(first: First, second: First) {}
        fn main() { consume(First, fail(2147483647)); println!("unreachable"); }
        """, "first\n", expectedSuccess: false);

    private static Task NestedNormalFailuresAsync() => CompileAndRunAsync("""
        fn overflow(value: i32) -> i32 { value + 1 }
        fn divide(value: i32) -> i32 { 10 / value }
        struct Bad { divisor: i32 }
        impl Drop for Bad {
            fn drop(&mut self) { println!("bad"); println!("{}", divide(self.divisor)); }
        }
        struct Good;
        impl Drop for Good { fn drop(&mut self) { println!("good"); } }
        struct Owner { bad: Bad, good: Good }
        impl Drop for Owner {
            fn drop(&mut self) { println!("owner"); println!("{}", overflow(2147483647)); }
        }
        fn main() { let owner = Owner { bad: Bad { divisor: 0 }, good: Good }; println!("body"); }
        """, "body\nowner\nbad\ngood\n", expectedSuccess: false,
        expectedError: nameof(RustGeneratedCleanupException), expectedAdditionalError: nameof(OverflowException));

    private static Task NestedUnwindOwnerFailureAsync() => CompileAndRunAsync("""
        fn overflow(value: i32) -> i32 { value + 1 }
        fn divide(value: i32) -> i32 { 10 / value }
        struct Bad;
        impl Drop for Bad {
            fn drop(&mut self) { println!("bad"); println!("{}", overflow(2147483647)); }
        }
        struct Good;
        impl Drop for Good { fn drop(&mut self) { println!("good"); } }
        struct Owner { divisor: i32, bad: Bad, good: Good }
        impl Drop for Owner {
            fn drop(&mut self) { println!("owner"); println!("{}", divide(self.divisor)); }
        }
        fn main() {
            let owner = Owner { divisor: 0, bad: Bad, good: Good };
            println!("body");
            println!("{}", overflow(2147483647));
            println!("unreachable");
        }
        """, "body\nowner\n", expectedSuccess: false,
        expectedError: "RustSharp double panic abort:", expectedExitCode: RustGeneratedPanic.AbortExitCode,
        expectedAdditionalError: "overflow. | Attempted to divide by zero.");

    private const string MutableEnumPrelude = MarkerPrelude + """
        enum Choice { One(Marker), Two(Marker, Marker), Empty }

        """;

    private static Task MutableEnumCallReplacementAsync() => CompileAndRunAsync(MutableEnumPrelude + """
        fn replace(value: &mut Choice) { *value = Choice::Two(make(2), make(3)); }
        fn main() {
            let retained = make(9);
            let mut choice = Choice::One(make(1));
            replace(&mut choice);
            println!("body");
        }
        """, "1\nbody\n2\n3\n9\n");

    private static Task MutableEnumCallReplacementFaultAsync() => CompileAndRunAsync(MutableEnumPrelude + """
        fn overflow(value: i32) -> i32 { value + 1 }
        fn replace(value: &mut Choice) {
            *value = Choice::Two(make(2), make(3));
            println!("callee");
            println!("{}", overflow(2147483647));
        }
        fn main() {
            let retained = make(9);
            let mut choice = Choice::One(make(1));
            println!("body");
            replace(&mut choice);
            println!("unreachable");
        }
        """, "body\n1\ncallee\n2\n3\n9\n", expectedSuccess: false);

    private static Task MutableEnumReplacementDropFaultAsync() => CompileAndRunAsync("""
        struct Marker { value: i32 }
        fn overflow(value: i32) -> i32 { value + 1 }
        impl Drop for Marker {
            fn drop(&mut self) {
                println!("{}", self.value);
                if self.value == 1 { println!("{}", overflow(2147483647)); }
            }
        }
        fn make(value: i32) -> Marker { Marker { value: value } }
        enum Choice { One(Marker), Two(Marker) }
        fn replace(value: &mut Choice) { *value = Choice::Two(make(2)); }
        fn main() {
            let retained = make(9);
            let mut choice = Choice::One(make(1));
            println!("body");
            replace(&mut choice);
            println!("after");
        }
        """, "body\n1\n2\n9\n", expectedSuccess: false, forbidAbort: true);

    private static async Task CompileAndRunAsync(
        string source, string expected, bool expectedSuccess = true,
        string expectedError = nameof(OverflowException), int? expectedExitCode = null,
        string? expectedAdditionalError = null, bool forbidAbort = false)
    {
        string ownedRoot = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(ownedRoot, "p1-aggregate-drop-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("Aggregate Drop test directory already exists: " + directory);
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source, sourcePath, outputPath, "P1AggregateDrop", CompilationProfile.SafeCoreMirV2,
                compileDeadline.Token);
            AssertEx.True(compiled.Success, "Generated aggregate Drop compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult result = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10), started =>
                    Console.WriteLine($"generated aggregate Drop process: pid={started.ProcessId} parent={started.ParentProcessId} started={started.StartedAt:O} command={started.CommandLine}")),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.False(result.ProcessTreeCleanupIncomplete,
                "Generated aggregate Drop process cleanup failed: " + result.ProcessTreeCleanupDiagnostic);
            AssertEx.Equal(BoundedProcessTermination.Exited, result.Termination,
                "A deadline or cancellation cannot count as an expected panic.");
            AssertEx.Equal(expectedSuccess, result.Succeeded,
                "Generated aggregate Drop exit behavior differed: " + result.StandardError);
            if (!expectedSuccess)
                AssertEx.True(result.StandardError.Contains(expectedError, StringComparison.Ordinal),
                    "Generated aggregate cleanup must report its declared failure: " + result.StandardError);
            if (expectedExitCode is { } exitCode)
                AssertEx.Equal(exitCode, result.ExitCode ?? -1, "Generated aggregate cleanup used the wrong exit category.");
            if (expectedAdditionalError is { } additionalError)
                AssertEx.True(result.StandardError.Contains(additionalError, StringComparison.Ordinal),
                    "Generated aggregate cleanup must retain its failure evidence: " + result.StandardError);
            if (forbidAbort)
                AssertEx.False(result.ExitCode == RustGeneratedPanic.AbortExitCode ||
                    result.StandardError.Contains("RustSharp double panic abort:", StringComparison.Ordinal) ||
                    result.StandardError.Contains(nameof(RustGeneratedAbortException), StringComparison.Ordinal),
                    "A consumed borrowed replacement must propagate one original panic without double-panic abort: " + result.StandardError);
            AssertEx.Equal(expected, result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            await DeleteOwnedDirectoryAsync(directory, ownedRoot).ConfigureAwait(false);
        }
    }

    private static async Task DeleteOwnedDirectoryAsync(string directory, string ownedRoot)
    {
        const string prefix = "p1-aggregate-drop-";
        string fullPath = Path.GetFullPath(directory);
        string fullRoot = Path.GetFullPath(ownedRoot);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string name = Path.GetFileName(fullPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullRoot, comparison) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _))
            throw new InvalidOperationException("Refusing to clean an unowned aggregate Drop test directory: " + fullPath);
        if (!Directory.Exists(fullPath)) return;
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to clean a redirected aggregate Drop test directory: " + fullPath);
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
        throw new IOException("Aggregate Drop test cleanup exceeded 40 attempts or five seconds: " + fullPath, lastFailure);
    }
}
