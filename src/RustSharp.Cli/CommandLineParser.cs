namespace RustSharp.Cli;

internal static class CommandLineParser
{
    private const int MaximumArgumentCount = 64;

    public static CommandLineParseResult Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Length > MaximumArgumentCount)
        {
            return Failure($"rsc accepts at most {MaximumArgumentCount} arguments.");
        }

        if (arguments.Length == 0 || arguments[0] is "help" or "--help" or "-h")
        {
            return Success(new CommandLineOptions(CommandKind.Help));
        }

        if (arguments[0] is "--version" or "-V")
        {
            return Success(new CommandLineOptions(CommandKind.Version));
        }

        var command = arguments[0] switch
        {
            "check" => CommandKind.Check,
            "compile" => CommandKind.Compile,
            "run" => CommandKind.Run,
            "publish" => CommandKind.Publish,
            _ => (CommandKind?)null,
        };

        if (command is null)
        {
            return Failure($"Unknown command '{arguments[0]}'.");
        }

        string? sourcePath = null;
        string? outputPath = null;
        string? runtimeIdentifier = null;
        var timeoutSeconds = 600;

        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--output" or "-o":
                    if (!TryTakeValue(arguments, ref index, out outputPath))
                    {
                        return Failure($"Option '{argument}' requires a value.");
                    }

                    break;

                case "--runtime" or "-r":
                    if (!TryTakeValue(arguments, ref index, out runtimeIdentifier))
                    {
                        return Failure($"Option '{argument}' requires a value.");
                    }

                    break;

                case "--timeout":
                    if (!TryTakeValue(arguments, ref index, out var timeoutText)
                        || !int.TryParse(timeoutText, out timeoutSeconds)
                        || timeoutSeconds is < 1 or > 3600)
                    {
                        return Failure("Option '--timeout' requires an integer from 1 to 3600.");
                    }

                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        return Failure($"Unknown option '{argument}'.");
                    }

                    if (sourcePath is not null)
                    {
                        return Failure("Only one source file can be compiled in this milestone.");
                    }

                    sourcePath = argument;
                    break;
            }
        }

        if (sourcePath is null)
        {
            return Failure($"Command '{arguments[0]}' requires a RustSharp source file.");
        }

        if (command != CommandKind.Publish && runtimeIdentifier is not null)
        {
            return Failure("Option '--runtime' is valid only for the publish command.");
        }

        if (command == CommandKind.Check && outputPath is not null)
        {
            return Failure("The check command does not produce an output file.");
        }

        return Success(new CommandLineOptions(
            command.Value,
            sourcePath,
            outputPath,
            runtimeIdentifier,
            timeoutSeconds));
    }

    private static bool TryTakeValue(
        string[] arguments,
        ref int index,
        out string? value)
    {
        if (index + 1 >= arguments.Length)
        {
            value = null;
            return false;
        }

        index++;
        value = arguments[index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static CommandLineParseResult Success(CommandLineOptions options) => new(options, null);

    private static CommandLineParseResult Failure(string error) => new(null, error);
}
