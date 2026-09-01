namespace RustSharp.Tests;

internal sealed record TestCase(string Name, Func<Task> ExecuteAsync);
