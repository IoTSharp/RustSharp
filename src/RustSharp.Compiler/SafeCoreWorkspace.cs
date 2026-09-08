using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>Loads only declared Rust modules; Cargo, package discovery and attribute evaluation are unsupported.</summary>
public static class SafeCoreWorkspace
{
    /// <summary>Expands bounded, strictly UTF-8 source files. Caller cancellation throws; other loading failures return diagnostics.</summary>
    public static SafeCoreWorkspaceResult Load(
        string entryPath, SafeCoreWorkspaceOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new SafeCoreWorkspaceOptions();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Workspace timeout must be positive and at most one minute.");
        }

        options = options with
        {
            MaximumFiles = Math.Clamp(options.MaximumFiles, 1, 1024),
            MaximumFileBytes = Math.Clamp(options.MaximumFileBytes, 1, 16_000_000),
            MaximumTotalBytes = Math.Clamp(options.MaximumTotalBytes, 1, 32_000_000),
            MaximumExpandedLength = Math.Clamp(options.MaximumExpandedLength, 1, 1_000_000),
            MaximumModuleDepth = Math.Clamp(options.MaximumModuleDepth, 1, 128),
            MaximumOperations = Math.Clamp(options.MaximumOperations, 1, 4_000_000),
        };
        return new Loader(entryPath, options, cancellationToken).Run();
    }

    private sealed class Loader(string entryPath, SafeCoreWorkspaceOptions options, CancellationToken cancellationToken)
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly List<SafeCoreSourceDocument> _documents = [];
        private readonly List<SafeCoreSourceMapSegment> _segments = [];
        private readonly Dictionary<string, (SafeCoreSourceDocument Document, SafeCoreSyntaxResult Syntax)> _files =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly StringBuilder _expanded = new();
        private readonly List<Diagnostic> _diagnostics = [];
        private string _rootDirectory = string.Empty;
        private string _currentPath = entryPath;
        private TextSpan _currentSpan;
        private int _operations;
        private int _totalBytes;

        internal SafeCoreWorkspaceResult Run()
        {
            try
            {
                Step();
                string path = Path.GetFullPath(_currentPath);
                _currentPath = path;
                _rootDirectory = Path.GetDirectoryName(path)!;
                CheckPath(path);
                ExpandFile(path, _rootDirectory, 0, isEntry: true);
                Step();
                if (_diagnostics.Count == 0)
                {
                    var sourceMap = new SafeCoreSourceMap(_documents, _segments, _expanded.Length, cancellationToken);
                    Step();
                    return new SafeCoreWorkspaceResult(_expanded.ToString(), sourceMap, Array.Empty<Diagnostic>());
                }
            }
            catch (WorkspaceException exception)
            {
                Report(exception.Code, exception.Message);
            }
            catch (TimeoutException)
            {
                Report(SafeCoreWorkspaceDiagnosticCodes.LimitReached, "Workspace loading exceeded its time budget.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Report(SafeCoreWorkspaceDiagnosticCodes.LimitReached, "Workspace loading exceeded its time budget.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException or NotSupportedException)
            {
                Report(SafeCoreWorkspaceDiagnosticCodes.InvalidSource, "Cannot load source: " + exception.Message);
            }

            return new SafeCoreWorkspaceResult(string.Empty, null, _diagnostics.AsReadOnly());
        }

        private void ExpandFile(string path, string moduleDirectory, int depth, bool isEntry)
        {
            _currentPath = path;
            _currentSpan = default;
            Step();
            CheckDepth(depth);
            CheckPath(path);
            if (!_files.TryGetValue(path, out var file))
            {
                if (_documents.Count >= options.MaximumFiles) Limit("Workspace file count exceeded its limit.");
                byte[] bytes = ReadBytes(path);
                int offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                string text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
                var document = new SafeCoreSourceDocument(path, text, bytes);
                SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(text, path,
                    new SafeCoreSyntaxOptions
                    {
                        Timeout = RemainingTime(),
                        MaximumSourceLength = options.MaximumExpandedLength,
                        MaximumOperations = Math.Min(options.MaximumOperations, 1_000_000),
                    }, cancellationToken);
                Step();
                if (!syntax.IsSuccessful)
                {
                    foreach (Diagnostic diagnostic in syntax.Diagnostics)
                    {
                        Step();
                        _diagnostics.Add(diagnostic with { SourcePath = path });
                    }
                    return;
                }

                RejectAttributes(syntax.Root!.Attributes);

                _documents.Add(document);
                file = (document, syntax);
                _files.Add(path, file);
            }

            string content = file.Document.Text;
            if (!isEntry)
            {
                foreach (RustTrivia trivia in file.Syntax.LexResult.Trivia)
                {
                    Step();
                    if (trivia.Kind == RustTriviaKind.Shebang)
                    {
                        content = content.Remove(trivia.Span.Start, trivia.Span.Length)
                            .Insert(trivia.Span.Start, new string(' ', trivia.Span.Length));
                        break;
                    }
                }
            }

            int cursor = 0;
            ExpandItems(file.Document, content, file.Syntax.Root!.Items, moduleDirectory, depth, ref cursor);
            if (_diagnostics.Count == 0) AppendOriginal(file.Document, content, cursor, content.Length - cursor);
        }

        private void ExpandItems(SafeCoreSourceDocument document, string content,
            IReadOnlyList<SafeCoreItemSyntax> items, string moduleDirectory, int depth, ref int cursor)
        {
            foreach (SafeCoreItemSyntax item in items)
            {
                _currentPath = document.Path;
                _currentSpan = item.Span;
                Step();
                if (_diagnostics.Count != 0) return;
                if (item is not SafeCoreModuleSyntax module) continue;
                CheckDepth(depth + 1);
                RejectAttributes(module.Attributes);
                RejectAttributes(module.InnerAttributes);

                string name = module.Name.StartsWith("r#", StringComparison.Ordinal) ? module.Name[2..] : module.Name;
                name = name.Normalize(NormalizationForm.FormC);
                if (name is "" or "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
                {
                    throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.UnsafePath, "Module name cannot be used as a source path.");
                }

                string childDirectory = Path.Combine(moduleDirectory, name);
                if (!module.IsExternal)
                {
                    ExpandItems(document, content, module.Items, childDirectory, depth + 1, ref cursor);
                    continue;
                }

                string flatPath = Path.Combine(moduleDirectory, name + ".rs");
                string nestedPath = Path.Combine(childDirectory, "mod.rs");
                CheckPath(flatPath);
                CheckPath(nestedPath);
                bool flatExists = File.Exists(flatPath);
                bool nestedExists = File.Exists(nestedPath);
                Step();
                if (!flatExists && !nestedExists)
                {
                    throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.MissingModule,
                        $"Module '{name}' requires '{flatPath}' or '{nestedPath}'.");
                }
                if (flatExists && nestedExists)
                {
                    throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.AmbiguousModule,
                        $"Module '{name}' has both '{flatPath}' and '{nestedPath}'.");
                }

                int semicolon = module.Span.End - 1;
                if (semicolon < cursor || semicolon >= content.Length || content[semicolon] != ';')
                {
                    throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.InvalidSource, "External module has no terminal semicolon.");
                }
                AppendOriginal(document, content, cursor, semicolon - cursor);
                AppendSynthetic(document, semicolon, "{\n");
                ExpandFile(flatExists ? flatPath : nestedPath, childDirectory, depth + 1, isEntry: false);
                if (_diagnostics.Count != 0) return;
                _currentPath = document.Path;
                _currentSpan = module.Span;
                AppendSynthetic(document, semicolon, "\n}");
                cursor = semicolon + 1;
            }
        }

        private byte[] ReadBytes(string path)
        {
            Step();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RemainingTime());
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            if (length > options.MaximumFileBytes || length > options.MaximumTotalBytes - _totalBytes)
            {
                Limit("Workspace source byte count exceeded its limit.");
            }
            byte[] bytes = new byte[(int)length];
            stream.ReadExactlyAsync(bytes.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
            byte[] extra = new byte[1];
            if (stream.ReadAsync(extra.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult() != 0)
            {
                throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.InvalidSource, "Source changed size while loading.");
            }
            _totalBytes += bytes.Length;
            Step();
            return bytes;
        }

        private void RejectAttributes(IReadOnlyList<SafeCoreAttributeSyntax> attributes)
        {
            foreach (SafeCoreAttributeSyntax attribute in attributes)
            {
                Step();
                if (!attribute.IsDocumentation)
                {
                    _currentSpan = attribute.Span;
                    throw new WorkspaceException(SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax,
                        "Module attributes require semantics outside the current workspace profile.");
                }
            }
        }

        private void CheckPath(string path)
        {
            Step();
            string fullPath = Path.GetFullPath(path);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string rootPrefix = Path.TrimEndingDirectorySeparator(_rootDirectory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, comparison) || fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.UnsafePath, "Source path must remain under the local workspace root.");
            }

            string volume = Path.GetPathRoot(fullPath)!;
            string[] parts = fullPath[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 256) Limit("Source path nesting exceeded its limit.");
            string component = volume;
            foreach (string part in parts)
            {
                Step();
                component = Path.Combine(component, part);
                try
                {
                    if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.UnsafePath,
                            "Symbolic links and reparse points are not allowed in workspace source paths.");
                    }
                }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
            }
        }

        private void AppendOriginal(SafeCoreSourceDocument document, string content, int start, int length)
        {
            Step();
            if (length == 0) return;
            CheckLength(length);
            _segments.Add(new SafeCoreSourceMapSegment(new TextSpan(_expanded.Length, length), document, new TextSpan(start, length)));
            _expanded.Append(content, start, length);
        }

        private void AppendSynthetic(SafeCoreSourceDocument document, int semicolon, string text)
        {
            Step();
            CheckLength(text.Length);
            _segments.Add(new SafeCoreSourceMapSegment(new TextSpan(_expanded.Length, text.Length), document,
                new TextSpan(semicolon, 1), IsSynthetic: true));
            _expanded.Append(text);
        }

        private void CheckLength(int length)
        {
            if (length > options.MaximumExpandedLength - _expanded.Length) Limit("Expanded source length exceeded its limit.");
        }

        private void CheckDepth(int depth)
        {
            if (depth > options.MaximumModuleDepth) Limit("Workspace module nesting exceeded its limit.");
        }

        private void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_operations > options.MaximumOperations) Limit("Workspace operation count exceeded its limit.");
            _ = RemainingTime();
        }

        private TimeSpan RemainingTime()
        {
            TimeSpan remaining = options.Timeout - Stopwatch.GetElapsedTime(_startedAt);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            return remaining;
        }

        private void Report(string code, string message) =>
            _diagnostics.Add(new Diagnostic(code, message, _currentSpan) { SourcePath = _currentPath });

        private static void Limit(string message) => throw new WorkspaceException(SafeCoreWorkspaceDiagnosticCodes.LimitReached, message);
    }

    private sealed class WorkspaceException(string code, string message) : Exception(message)
    {
        internal string Code { get; } = code;
    }
}
