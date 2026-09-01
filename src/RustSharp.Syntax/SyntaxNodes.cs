using System.Collections.Generic;

namespace RustSharp.Syntax;

public sealed record PrintStatementSyntax(string Value, TextSpan Span);

public sealed record CompilationUnitSyntax(IReadOnlyList<PrintStatementSyntax> Statements);
