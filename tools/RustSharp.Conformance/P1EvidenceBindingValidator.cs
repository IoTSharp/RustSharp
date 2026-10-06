using System.Security.Cryptography;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RustSharp.Conformance;

/// <summary>
/// Strict validator for P1 platform/differential evidence provenance.
/// A report is valid only when every required provenance and execution field
/// is present and the fixed denominator is closed with passed cases.
/// </summary>
internal static class P1EvidenceBindingValidator
{
    internal const int MaximumReportBytes = 16 * 1024 * 1024;
    internal const int MaximumCaseCount = 256;
    private const int MaximumJsonTokens = 65536;

    // Supplied by the caller after validating the frozen manifest, never read
    // from the report being checked.
    internal sealed record CaseBinding(string Id, string Source, string SourceSha256, string ExpectationSha256, string ExpectedOutput);

    internal sealed record BindingExpectation(
        string EvidenceKind,
        string Profile,
        string RuntimeIdentifier,
        int Denominator,
        string ManifestSha256,
        string CompilerSha256,
        string OracleVersionPrefix = "rustc 1.98.0 (",
        string? CandidateSha = null,
        IReadOnlyList<CaseBinding>? CaseBindings = null,
        bool RequireSemanticClosure = false,
        string? ObservedRuntimeIdentifier = null);

    internal sealed record ValidationResult(
        bool Valid,
        string Status,
        IReadOnlyList<string> Errors)
    {
        internal static ValidationResult Pass() => new(true, "passed", []);
        internal static ValidationResult Fail(IEnumerable<string> errors) =>
            new(false, "failed", errors.ToArray());
    }

    internal static ValidationResult ValidateFile(string path, BindingExpectation expectation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo info = new(path);
        if (!info.Exists)
            return ValidationResult.Fail(["Evidence report does not exist."]);
        if (info.Length is < 1 or > MaximumReportBytes)
            return ValidationResult.Fail(["Evidence report exceeds its byte bound."]);
        try
        {
            return Validate(File.ReadAllBytes(path), expectation);
        }
        catch (IOException exception)
        {
            return ValidationResult.Fail(["Evidence report could not be read: " + exception.Message]);
        }
    }

    internal static ValidationResult Validate(ReadOnlySpan<byte> bytes, BindingExpectation expectation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        if (bytes.Length is < 1 or > MaximumReportBytes)
            return ValidationResult.Fail(["Evidence report exceeds its byte bound."]);

        var errors = new List<string>();
        Stopwatch validationClock = Stopwatch.StartNew();
        try
        {
            CheckValidationBudget(validationClock, cancellationToken);
            ValidateJsonShape(bytes, validationClock, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ValidationResult.Fail(["Evidence report root must be an object."]);
            RequireString(root, "evidenceKind", expectation.EvidenceKind, errors);
            RequireString(root, "profile", expectation.Profile, errors);
            ValidateManifest(root, expectation, errors);
            ValidateCompiler(root, expectation, errors);
            ValidateCandidate(root, expectation, errors);
            ValidatePlatform(root, expectation, errors);
            ValidateToolVersions(root, expectation, errors);
            ValidateSummaryAndCases(root, expectation, errors, validationClock, cancellationToken);
            ValidateExecutionAndCleanup(root, errors);
            if (expectation.Profile == P1ExpandedPlatformRunner.ProfileName)
                ValidatePlatformExecution(root, expectation, errors, validationClock, cancellationToken);
            CheckValidationBudget(validationClock, cancellationToken);
        }
        catch (JsonException exception)
        {
            errors.Add("Evidence report is not valid JSON: " + exception.Message);
        }

        return errors.Count == 0 ? ValidationResult.Pass() : ValidationResult.Fail(errors);
    }

    private static void ValidateJsonShape(ReadOnlySpan<byte> bytes, Stopwatch validationClock, CancellationToken cancellationToken)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>>();
        for (int token = 0; token < MaximumJsonTokens; token++)
        {
            CheckValidationBudget(validationClock, cancellationToken);
            if (!reader.Read()) return;
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new JsonException("Duplicate JSON properties are forbidden.");
        }
        throw new JsonException("Evidence JSON exceeds its token or validation time bound.");
    }

    private static void CheckValidationBudget(Stopwatch clock, CancellationToken cancellationToken)
    {
        if (clock.Elapsed >= TimeSpan.FromSeconds(5) || cancellationToken.IsCancellationRequested)
            throw new JsonException("Evidence validation exceeded its wall-clock deadline or was cancelled.");
    }

    internal static ValidationResult Validate(string json, BindingExpectation expectation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Validate(System.Text.Encoding.UTF8.GetBytes(json), expectation, cancellationToken);
    }

    private static void ValidateManifest(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("manifest", out JsonElement manifest) || manifest.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Manifest provenance is missing.");
            return;
        }
        RequireHash(manifest, "sha256", expected.ManifestSha256, "manifest", errors);
        RequirePositiveInt(manifest, "denominator", expected.Denominator, "manifest", errors);
        RequireBoolean(manifest, "validated", true, "manifest", errors);
    }

    private static void ValidateCompiler(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("compiler", out JsonElement compiler) || compiler.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Compiler provenance is missing.");
            return;
        }
        RequireHash(compiler, "sha256", expected.CompilerSha256, "compiler", errors);
        RequireNonEmpty(compiler, "profile", "compiler", errors);
    }

    private static void ValidateCandidate(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(expected.CandidateSha)) return;
        if (!IsCandidateSha(expected.CandidateSha))
        {
            errors.Add("The expected candidate SHA must be a 40-64 character hexadecimal commit identifier.");
            return;
        }
        string? actual = OptionalString(root, "candidateSha");
        if (!IsCandidateSha(actual)) errors.Add("candidateSha must be a 40-64 character hexadecimal commit identifier.");
        else if (!actual!.Equals(expected.CandidateSha, StringComparison.OrdinalIgnoreCase)) errors.Add("candidateSha does not match the expected candidate hash.");
    }

    private static void ValidatePlatform(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("platform", out JsonElement platform) || platform.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Platform provenance is missing.");
            return;
        }
        RequireString(platform, "runtimeIdentifier", expected.RuntimeIdentifier, errors, "platform");
        RequireNonEmpty(platform, "name", "platform", errors);
    }

    private static void ValidateToolVersions(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("toolVersions", out JsonElement versions) || versions.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Tool version provenance is missing.");
            return;
        }
        string? rustc = OptionalString(versions, "rustc");
        if (rustc is null || !rustc.StartsWith(expected.OracleVersionPrefix, StringComparison.Ordinal))
            errors.Add("Tool version rustc does not identify the required stable 1.98.0 oracle.");
        RequireNonEmpty(versions, "dotnet", "toolVersions", errors);
        RequireNonEmpty(versions, "sdkVersion", "toolVersions", errors);
    }

    private static void ValidateSummaryAndCases(JsonElement root, BindingExpectation expected, List<string> errors, Stopwatch clock, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Summary is missing.");
            return;
        }
        RequireString(summary, "status", "passed", errors, "summary");
        RequirePositiveInt(summary, "denominator", expected.Denominator, "summary", errors);
        RequirePositiveInt(summary, "executed", expected.Denominator, "summary", errors);
        RequirePositiveInt(summary, "passed", expected.Denominator, "summary", errors);
        RequirePositiveInt(summary, "failed", 0, "summary", errors, allowZero: true);
        RequirePositiveInt(summary, "blocked", 0, "summary", errors, allowZero: true);
        RequirePositiveInt(summary, "skipped", 0, "summary", errors, allowZero: true);
        if (summary.TryGetProperty("exitCode", out JsonElement exitCode) &&
            (exitCode.ValueKind != JsonValueKind.Number || !exitCode.TryGetInt32(out int code) || code != 0))
            errors.Add("Summary exitCode must be zero for passed evidence.");

        if (!root.TryGetProperty("cases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Case evidence is missing.");
            return;
        }
        if (cases.GetArrayLength() != expected.Denominator || cases.GetArrayLength() > MaximumCaseCount)
        {
            errors.Add("Case count does not equal the fixed denominator.");
            return;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<CaseBinding>? bindings = expected.CaseBindings;
        bool validBindings = bindings is { Count: > 0 and <= MaximumCaseCount } && bindings.Count == expected.Denominator &&
            bindings.All(static binding => !string.IsNullOrWhiteSpace(binding.Id) && !string.IsNullOrWhiteSpace(binding.Source) &&
                IsSha256(binding.SourceSha256) && IsSha256(binding.ExpectationSha256) && binding.ExpectedOutput is not null) &&
            bindings.Select(static binding => binding.Id).Distinct(StringComparer.Ordinal).Count() == bindings.Count;
        if (bindings is not null && !validBindings || expected.Profile == P1ExpandedPlatformRunner.ProfileName && !validBindings)
            errors.Add("Trusted case bindings must close the fixed denominator with unique frozen identities and hashes.");
        Dictionary<string, CaseBinding>? byId = validBindings ? bindings!.ToDictionary(static binding => binding.Id, StringComparer.Ordinal) : null;
        foreach (JsonElement item in cases.EnumerateArray())
        {
            CheckValidationBudget(clock, cancellationToken);
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Case entry must be an object.");
                continue;
            }
            string? id = OptionalString(item, "id");
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) errors.Add("Case IDs must be non-empty and unique.");
            RequireString(item, "status", "passed", errors, "case");
            string? sourceHash = OptionalString(item, "sourceSha256");
            if (!IsSha256(sourceHash)) errors.Add("Every case must contain a valid sourceSha256.");
            if (byId is not null)
            {
                if (id is null || !byId.TryGetValue(id, out CaseBinding? binding)) errors.Add("Case ID is outside the trusted fixed inventory.");
                else
                {
                    RequireString(item, "source", binding.Source, errors, "case");
                    RequireHash(item, "sourceSha256", binding.SourceSha256, "case", errors);
                    RequireHash(item, "expectationSha256", binding.ExpectationSha256, "case", errors);
                    if (expected.Profile == P1ExpandedPlatformRunner.ProfileName) ValidatePlatformCase(item, binding, root, errors, clock, cancellationToken);
                }
            }
        }
    }

    private static void ValidatePlatformExecution(JsonElement root, BindingExpectation expected, List<string> errors, Stopwatch clock, CancellationToken cancellationToken)
    {
        RequireBoolean(root, "semanticClosureEligible", expected.RequireSemanticClosure, "report", errors);
        if (expected.RuntimeIdentifier is not ("win-x64" or "linux-x64")) errors.Add("Platform runtime identifier must identify a supported native x64 host.");
        if (TryObject(root, "platform", out JsonElement platform, errors))
        {
            RequireString(platform, "name", expected.RuntimeIdentifier == "win-x64" ? "windows-x64" : "linux-x64", errors, "platform");
            RequireString(platform, "observedRuntimeIdentifier", expected.ObservedRuntimeIdentifier ?? expected.RuntimeIdentifier, errors, "platform");
            RequireString(platform, "architecture", "X64", errors, "platform");
            RequireString(platform, "processArchitecture", "X64", errors, "platform");
            RequireBoolean(platform, "nativeExecution", true, "platform", errors);
        }
        if (TryObject(root, "limits", out JsonElement limits, errors))
        {
            RequirePositiveInt(limits, "maximumCases", expected.Denominator, "limits", errors);
            RequirePositiveInt(limits, "maximumOutputBytes", RustSharp.Compiler.BoundedProcessRunner.MaximumTotalOutputBytes, "limits", errors);
            RequireBoundedNumber(limits, "caseTimeoutSeconds", 300, false, errors);
            RequireBoundedNumber(limits, "deadlineSeconds", 900, false, errors);
        }
        if (TryObject(root, "execution", out JsonElement execution, errors))
        {
            RequireBoolean(execution, "deadlineExpired", false, "execution", errors);
            bool startValid = TryTimestamp(execution, "startedAtUtc", out DateTimeOffset started);
            bool finishValid = TryTimestamp(execution, "finishedAtUtc", out DateTimeOffset finished);
            if (!startValid || !finishValid || finished < started)
                errors.Add("Execution timestamps must be ordered UTC values.");
            if (root.TryGetProperty("limits", out JsonElement timeLimits) && timeLimits.ValueKind == JsonValueKind.Object && TryNumber(timeLimits, "deadlineSeconds", out double deadline) && (finished - started).TotalSeconds > deadline + 10)
                errors.Add("Execution timestamps exceed the bounded platform deadline and cleanup grace.");
        }
        if (TryObject(root, "toolVersions", out JsonElement versions, errors) && TryObject(root, "preflight", out JsonElement preflight, errors))
        {
            foreach (string tool in new[] { "dotnet", "rustc", "ilverify" })
            {
                CheckValidationBudget(clock, cancellationToken);
                if (!TryObject(preflight, tool, out JsonElement process, errors)) continue;
                ValidateProcess(process, "preflight." + tool, root, errors);
                string? version = OptionalString(versions, tool == "dotnet" ? "sdkVersion" : tool);
                if (string.IsNullOrWhiteSpace(version) || version == "unavailable" || OptionalString(process, "standardOutput")?.Trim() != version)
                    errors.Add("Tool version must match its successful captured preflight: " + tool);
            }
            string? ilverify = OptionalString(versions, "ilverify");
            if (ilverify is null || !ilverify.Contains("10.0.11", StringComparison.Ordinal)) errors.Add("ILVerify preflight must identify the pinned 10.0.11 tool.");
        }
        if (TryObject(root, "summary", out JsonElement summary, errors)) RequirePositiveInt(summary, "exitCode", 0, "summary", errors, allowZero: true);
    }

    private static void ValidatePlatformCase(JsonElement item, CaseBinding binding, JsonElement root, List<string> errors, Stopwatch clock, CancellationToken cancellationToken)
    {
        if (TryObject(item, "coreClrCompile", out JsonElement compile, errors)) ValidateProcess(compile, "coreClrCompile", root, errors);
        if (TryObject(item, "coreClrRun", out JsonElement run, errors)) ValidateOutputProcess(run, "coreClrRun", binding.ExpectedOutput, root, errors);
        string? assemblyHash = OptionalString(item, "assemblySha256");
        if (!IsSha256(assemblyHash)) errors.Add("Case assemblySha256 must identify the generated PE verified and published.");
        if (TryObject(item, "ilVerify", out JsonElement ilverify, errors))
        {
            RequireString(ilverify, "status", "passed", errors, "ilVerify");
            RequireBoolean(ilverify, "succeeded", true, "ilVerify", errors);
            if (TryObject(ilverify, "process", out JsonElement process, errors)) ValidateProcess(process, "ilVerify.process", root, errors);
            ValidateIlVerifyDocument(ilverify, assemblyHash, root, errors, clock, cancellationToken);
        }
        if (TryObject(item, "nativeAot", out JsonElement aot, errors))
        {
            RequireString(aot, "status", "passed", errors, "nativeAot");
            RequireBoolean(aot, "succeeded", true, "nativeAot", errors);
            RequireBoolean(aot, "hostCleanupIncomplete", false, "nativeAot", errors);
            RequireBoolean(aot, "outputMatches", true, "nativeAot", errors);
            RequireNonEmpty(aot, "executablePath", "nativeAot", errors);
            if (!IsSha256(OptionalString(aot, "executableSha256"))) errors.Add("Native AOT executableSha256 is missing or invalid.");
            RequireHash(aot, "assemblySha256", assemblyHash ?? "", "nativeAot", errors);
            if (TryObject(aot, "publish", out JsonElement publish, errors)) ValidateProcess(publish, "nativeAot.publish", root, errors);
            if (TryObject(aot, "run", out JsonElement nativeRun, errors)) ValidateOutputProcess(nativeRun, "nativeAot.run", binding.ExpectedOutput, root, errors);
        }
    }

    private static void ValidateIlVerifyDocument(JsonElement ilverify, string? assemblyHash, JsonElement root, List<string> errors, Stopwatch clock, CancellationToken cancellationToken)
    {
        string? json = OptionalString(ilverify, "evidenceJson");
        if (json is null || Encoding.UTF8.GetByteCount(json) > 1_048_576) { errors.Add("ILVerify raw evidence JSON is missing or exceeds its byte bound."); return; }
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        RequireHash(ilverify, "evidenceSha256", Convert.ToHexString(SHA256.HashData(bytes)), "ilVerify", errors);
        try
        {
            ValidateJsonShape(bytes, clock, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement evidence = document.RootElement;
            if (evidence.ValueKind != JsonValueKind.Object) { errors.Add("ILVerify evidence root must be an object."); return; }
            RequireBoolean(evidence, "Succeeded", true, "ILVerify evidence", errors);
            if (TryObject(evidence, "Assembly", out JsonElement assembly, errors)) RequireHash(assembly, "Sha256", assemblyHash ?? "", "ILVerify assembly", errors);
            if (TryObject(evidence, "Tool", out JsonElement tool, errors))
            {
                RequireString(tool, "PackageId", "dotnet-ilverify", errors, "ILVerify tool");
                RequireString(tool, "Version", "10.0.11", errors, "ILVerify tool");
            }
            if (TryObject(evidence, "VerifyProcess", out JsonElement process, errors))
            {
                RequireString(process, "Termination", "Exited", errors, "ILVerify process");
                RequirePositiveInt(process, "ExitCode", 0, "ILVerify process", errors, allowZero: true);
                RequireNonEmpty(process, "CommandLine", "ILVerify process", errors);
                RequireIdentifier(process, "ProcessId", errors); RequireIdentifier(process, "ParentProcessId", errors);
                RequireBoundedNumber(process, "ElapsedMilliseconds", 310_000, true, errors);
                ValidateProcessInterval(process, "StartedAt", "ElapsedMilliseconds", root, errors, "ILVerify process");
                foreach (string flag in new[] { "StandardOutputTruncated", "StandardErrorTruncated", "OutputDrainTimedOut", "ProcessTreeCleanupIncomplete" }) RequireBoolean(process, flag, false, "ILVerify process", errors);
                if (ilverify.TryGetProperty("process", out JsonElement launcher) && launcher.ValueKind == JsonValueKind.Object &&
                    launcher.TryGetProperty("processId", out JsonElement launcherId) && launcherId.ValueKind == JsonValueKind.Number && launcherId.TryGetInt32(out int parentId))
                {
                    RequirePositiveInt(process, "ParentProcessId", parentId, "ILVerify process", errors);
                    if (TryTimestamp(process, "StartedAt", out DateTimeOffset childStarted) &&
                        TryNumber(process, "ElapsedMilliseconds", out double childElapsed) &&
                        TryTimestamp(launcher, "startedAtUtc", out DateTimeOffset parentStarted) &&
                        TryNumber(launcher, "elapsedMilliseconds", out double parentElapsed) &&
                        (childStarted < parentStarted || (childStarted - parentStarted).TotalMilliseconds + childElapsed > parentElapsed + 1000))
                        errors.Add("ILVerify process duration is outside its launcher execution interval.");
                }
            }
        }
        catch (JsonException exception) { errors.Add("ILVerify raw evidence JSON is invalid: " + exception.Message); }
    }

    private static void ValidateOutputProcess(JsonElement process, string scope, string expectedOutput, JsonElement root, List<string> errors)
    {
        ValidateProcess(process, scope, root, errors);
        RequireBoolean(process, "outputMatches", true, scope, errors);
        string? actual = OptionalString(process, "standardOutput");
        if (actual is null || NormalizeOutput(actual) != NormalizeOutput(expectedOutput)) errors.Add(scope + " captured output differs from the trusted frozen expectation.");
    }

    private static void ValidateProcess(JsonElement process, string scope, JsonElement root, List<string> errors)
    {
        RequireString(process, "termination", "exited", errors, scope);
        RequirePositiveInt(process, "exitCode", 0, scope, errors, allowZero: true);
        RequireNonEmpty(process, "commandLine", scope, errors);
        RequireNonEmpty(process, "fileName", scope, errors);
        RequireNonEmpty(process, "workingDirectory", scope, errors);
        RequireIdentifier(process, "processId", errors); RequireIdentifier(process, "parentProcessId", errors);
        foreach (string flag in new[] { "outputTruncated", "outputReadTimedOut", "outputDrainTimedOut", "outputReadLimitReached", "cleanupIncomplete" }) RequireBoolean(process, flag, false, scope, errors);
        if (!process.TryGetProperty("standardOutput", out JsonElement stdout) || stdout.ValueKind != JsonValueKind.String ||
            !process.TryGetProperty("standardError", out JsonElement stderr) || stderr.ValueKind != JsonValueKind.String)
            errors.Add(scope + " requires complete captured output streams.");
        if (!process.TryGetProperty("arguments", out JsonElement arguments) || arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 64 || arguments.EnumerateArray().Any(static argument => argument.ValueKind != JsonValueKind.String)) errors.Add(scope + " arguments are missing or invalid.");
        RequireBoundedNumber(process, "elapsedMilliseconds", 310_000, true, errors);
        if (root.TryGetProperty("limits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object &&
            TryNumber(limits, "caseTimeoutSeconds", out double timeout) && TryNumber(process, "elapsedMilliseconds", out double elapsed) && elapsed > timeout * 1000 + 10_000)
            errors.Add(scope + " process duration exceeds the configured case timeout and cleanup grace.");
        if (OptionalString(process, "standardOutput") is string capturedOutput && OptionalString(process, "standardError") is string capturedError &&
            Encoding.UTF8.GetByteCount(capturedOutput) + Encoding.UTF8.GetByteCount(capturedError) > RustSharp.Compiler.BoundedProcessRunner.MaximumTotalOutputBytes)
            errors.Add(scope + " captured output exceeds the declared process byte bound.");
        ValidateProcessInterval(process, "startedAtUtc", "elapsedMilliseconds", root, errors, scope);
    }

    private static void ValidateProcessInterval(JsonElement process, string startProperty, string elapsedProperty, JsonElement root, List<string> errors, string scope)
    {
        if (!TryTimestamp(process, startProperty, out DateTimeOffset started)) errors.Add(scope + " start time is invalid.");
        else if (root.TryGetProperty("execution", out JsonElement execution) && execution.ValueKind == JsonValueKind.Object &&
            TryTimestamp(execution, "startedAtUtc", out DateTimeOffset begin) && TryTimestamp(execution, "finishedAtUtc", out DateTimeOffset end))
        {
            if (started < begin || started > end) errors.Add(scope + " process start is outside the report execution interval.");
            // The monotonic duration starts just before the wall-clock start
            // record. Allow one second of scheduling skew, without adding to
            // untrusted timestamps (which could overflow DateTimeOffset).
            if (TryNumber(process, elapsedProperty, out double elapsed) && elapsed > (end - started).TotalMilliseconds + 1000)
                errors.Add(scope + " process duration extends beyond the report execution interval.");
        }
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value, List<string> errors)
    {
        if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object) return true;
        errors.Add(name + " object evidence is missing."); return false;
    }

    private static void RequireIdentifier(JsonElement parent, string name, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int id) || id < 1) errors.Add(name + " must identify a real positive process ID.");
    }

    private static bool TryTimestamp(JsonElement parent, string name, out DateTimeOffset value) => DateTimeOffset.TryParse(OptionalString(parent, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out value) && value.Offset == TimeSpan.Zero;
    private static bool TryNumber(JsonElement parent, string name, out double value) { value = 0; return parent.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out value) && double.IsFinite(value); }
    private static void RequireBoundedNumber(JsonElement parent, string name, double maximum, bool allowZero, List<string> errors)
    {
        if (!TryNumber(parent, name, out double value) || (allowZero ? value < 0 : value <= 0) || value > maximum) errors.Add(name + " exceeds its positive execution bound.");
    }
    private static string NormalizeOutput(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void ValidateExecutionAndCleanup(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("execution", out JsonElement execution) || execution.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Execution provenance is missing.");
        }
        else
        {
            RequireNonEmpty(execution, "startedAtUtc", "execution", errors);
            RequireNonEmpty(execution, "finishedAtUtc", "execution", errors);
            if (execution.TryGetProperty("deadlineExpired", out JsonElement expired) &&
                (expired.ValueKind != JsonValueKind.False))
                errors.Add("Execution deadlineExpired must be false.");
        }
        if (!root.TryGetProperty("cleanup", out JsonElement cleanup) || cleanup.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Cleanup provenance is missing.");
        }
        else
        {
            RequireBoolean(cleanup, "completed", true, "cleanup", errors);
            if (cleanup.TryGetProperty("diagnostic", out JsonElement diagnostic) && diagnostic.ValueKind != JsonValueKind.Null &&
                diagnostic.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(diagnostic.GetString()))
                errors.Add("Cleanup diagnostic must be empty for passed evidence.");
        }
    }

    private static void RequireHash(JsonElement parent, string name, string expected, string scope, List<string> errors)
    {
        string? actual = OptionalString(parent, name);
        if (!IsSha256(actual)) errors.Add($"{scope}.{name} must be a 64-character SHA-256 hash.");
        else if (!actual!.Equals(expected, StringComparison.OrdinalIgnoreCase)) errors.Add($"{scope}.{name} does not match the expected candidate hash.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private static bool IsCandidateSha(string? value) => value is { Length: >= 40 and <= 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private static void RequireString(JsonElement parent, string name, string expected, List<string> errors, string scope = "report")
    {
        if (OptionalString(parent, name) is not string actual || !actual.Equals(expected, StringComparison.Ordinal)) errors.Add($"{scope}.{name} does not match the expected value.");
    }

    private static void RequireNonEmpty(JsonElement parent, string name, string scope, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(OptionalString(parent, name))) errors.Add($"{scope}.{name} is missing or empty.");
    }

    private static void RequireBoolean(JsonElement parent, string name, bool expected, string scope, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False || value.GetBoolean() != expected)
            errors.Add($"{scope}.{name} must be {expected.ToString().ToLowerInvariant()}.");
    }

    private static void RequirePositiveInt(JsonElement parent, string name, int expected, string scope, List<string> errors, bool allowZero = false)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int actual) || actual != expected || (!allowZero && actual < 1))
            errors.Add($"{scope}.{name} must equal {expected}.");
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
