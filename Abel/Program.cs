using Abel.Core;
using CliWrap.Exceptions;
using System.Reflection;
using System.Text.Json;

namespace Abel;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitUsageError = 2;
    private const int ExitRuntimeError = 1;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var command = ParseCommand(args);

            return command.Kind switch
            {
                CommandKind.Help => PrintHelpAndReturnSuccess(),
                CommandKind.Version => PrintVersionAndReturnSuccess(),
                CommandKind.Check => await RunCheckAsync(command.Verbose).ConfigureAwait(false),
                CommandKind.Build => await RunBuildOrRunAsync(command, run: false).ConfigureAwait(false),
                CommandKind.Run => await RunBuildOrRunAsync(command, run: true).ConfigureAwait(false),
                CommandKind.List => RunListCommand(command),
                CommandKind.Search => RunSearchCommand(command),
                CommandKind.Info => RunInfoCommand(command),
                CommandKind.Init => RunInitCommand(command),
                CommandKind.Add => RunAddCommand(command),
                CommandKind.Module => RunModuleCommand(command),
                _ => ExitUsageError
            };
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Run 'abel help' for usage.").ConfigureAwait(false);
            return ExitUsageError;
        }
        catch (CommandExecutionException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitRuntimeError;
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitRuntimeError;
        }
        catch (IOException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitRuntimeError;
        }
        catch (UnauthorizedAccessException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitRuntimeError;
        }
    }

    private static int PrintHelpAndReturnSuccess()
    {
        Console.WriteLine("Abel - Opinionated C++ build runner");
        Console.WriteLine();
        PrintUsageSection();
        PrintPathBehaviorSection();
        PrintRegistryExamples();
        PrintInitExamples();
        PrintAddExamples();
        PrintModuleExamples();
        return ExitSuccess;
    }

    private static void PrintUsageSection()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  abel <command> [args...] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  build      Build one or more project directories");
        Console.WriteLine("  run        Build then run executable projects");
        Console.WriteLine("  check      Check required tools on PATH");
        Console.WriteLine("  list       List known registry packages");
        Console.WriteLine("  search     Search registry packages");
        Console.WriteLine("  info       Show detailed package metadata");
        Console.WriteLine("  init       Create a new Abel project from a template");
        Console.WriteLine("  add        Add dependency entries to project.json");
        Console.WriteLine("  module     Create a local module and add it as dependency");
        Console.WriteLine("  help       Show this help");
        Console.WriteLine("  version    Show tool version");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -v, --verbose   Enable verbose output");
        Console.WriteLine("  --release       Use Release configuration");
        Console.WriteLine("  --debug         Use Debug configuration");
        Console.WriteLine("  -c, --configuration <name>   Build config: Debug|Release|RelWithDebInfo|MinSizeRel");
        Console.WriteLine("                 Default: project.json build.default_configuration, else Release");
        Console.WriteLine();
    }

    private static void PrintPathBehaviorSection()
    {
        Console.WriteLine("Path behavior:");
        Console.WriteLine("  - If no paths are provided, current directory is used if it contains project.json.");
        Console.WriteLine("  - If a provided directory has no project.json, Abel scans immediate child directories.");
        Console.WriteLine("  - You can also pass a direct path to project.json.");
        Console.WriteLine();
    }

    private static void PrintRegistryExamples()
    {
        Console.WriteLine("Registry examples:");
        Console.WriteLine("  - abel list");
        Console.WriteLine("  - abel search sdl");
        Console.WriteLine("  - abel info imgui");
        Console.WriteLine("  - abel info imgui/sdl3_renderer");
        Console.WriteLine();
    }

    private static void PrintInitExamples()
    {
        Console.WriteLine("Init examples:");
        Console.WriteLine("  - abel init my_app");
        Console.WriteLine("  - abel init my_module --type module");
        Console.WriteLine("  - abel init --list-templates");
        Console.WriteLine();
    }

    private static void PrintAddExamples()
    {
        Console.WriteLine("Add examples:");
        Console.WriteLine("  - abel add sdl3");
        Console.WriteLine("  - abel add imgui/sdl3_renderer flecs");
        Console.WriteLine("  - abel add --project ./Game sdl3_image");
        Console.WriteLine();
    }

    private static void PrintModuleExamples()
    {
        Console.WriteLine("Module examples:");
        Console.WriteLine("  - abel module gameplay");
        Console.WriteLine("  - abel module ai --project ./Game/gameplay");
        Console.WriteLine("  - abel module ui --dir modules/ui");
        Console.WriteLine("  - abel module gameplay --partition ecs --partition systems.pathing");
    }

    private static int PrintVersionAndReturnSuccess()
    {
        var version = GetVersion();
        Console.WriteLine($"abel {version}");
        return ExitSuccess;
    }

    private static async Task<int> RunCheckAsync(bool verbose)
    {
        var ok = await ToolChecker.CheckAll(verbose).ConfigureAwait(false);
        return ok ? ExitSuccess : ExitRuntimeError;
    }

    private static async Task<int> RunBuildOrRunAsync(ParsedCommand command, bool run)
    {
        var projectDirectories = ResolveProjectDirectories(command.Arguments);
        if (projectDirectories.Count == 0)
            throw new InvalidOperationException("No project directories found.");

        using var runner = new AbelRunner(command.Verbose, command.BuildConfiguration);
        EventHandler onProcessExit = (_, _) => runner.Dispose();
        ConsoleCancelEventHandler onCancelKeyPress = (_, _) => runner.Dispose();
        AppDomain.CurrentDomain.ProcessExit += onProcessExit;
        Console.CancelKeyPress += onCancelKeyPress;

        foreach (var projectDirectory in projectDirectories)
            runner.ParseFolder(projectDirectory);

        try
        {
            if (run)
                await runner.Run().ConfigureAwait(false);
            else
                await runner.Build().ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
        }

        return ExitSuccess;
    }

    private static int RunListCommand(ParsedCommand command)
    {
        if (command.Arguments.Count > 0)
            throw new InvalidOperationException("Command 'list' does not accept arguments.");

        RegistryCli.ListPackages(command.Verbose);
        return ExitSuccess;
    }

    private static int RunSearchCommand(ParsedCommand command)
    {
        if (command.Arguments.Count == 0)
            throw new InvalidOperationException("Command 'search' requires a query. Example: abel search sdl");

        RegistryCli.SearchPackages(string.Join(' ', command.Arguments), command.Verbose);
        return ExitSuccess;
    }

    private static int RunInfoCommand(ParsedCommand command)
    {
        if (command.Arguments.Count != 1)
        {
            throw new InvalidOperationException(
                "Command 'info' requires exactly one package spec. Example: abel info imgui/sdl3_renderer");
        }

        return RegistryCli.TryPrintPackageInfo(command.Arguments[0], command.Verbose)
            ? ExitSuccess
            : ExitRuntimeError;
    }

    private static int RunInitCommand(ParsedCommand command)
    {
        var result = ProjectInitializer.TryInitialize(command.Arguments, command.Verbose);
        return result ? ExitSuccess : ExitUsageError;
    }

    private static int RunAddCommand(ParsedCommand command)
    {
        var result = DependencyAdder.TryAddDependencies(command.Arguments, command.Verbose);
        return result ? ExitSuccess : ExitUsageError;
    }

    private static int RunModuleCommand(ParsedCommand command)
    {
        var result = ModuleScaffolder.TryCreateModule(command.Arguments, command.Verbose);
        return result ? ExitSuccess : ExitUsageError;
    }

    private static ParsedCommand ParseCommand(string[] args)
    {
        if (args.Length == 0)
            return ParsedCommand.Help();

        var first = args[0];
        if (IsHelpToken(first))
            return ParsedCommand.Help();
        if (IsVersionToken(first))
            return ParsedCommand.Version();

        var kind = ParseCommandKind(first);

        var verbose = false;
        string? buildConfiguration = null;
        var paths = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (TryHandleCommonOption(kind, args, ref i, ref verbose, ref buildConfiguration, out var earlyExit))
            {
                if (earlyExit is not null)
                    return earlyExit;

                continue;
            }

            if (token.StartsWith('-') && kind != CommandKind.Init && kind != CommandKind.Add && kind != CommandKind.Module)
                throw new InvalidOperationException($"Unknown option '{token}'.");

            paths.Add(token);
        }

        return new ParsedCommand(kind, verbose, paths, buildConfiguration);
    }

    private static bool TryHandleCommonOption(
        CommandKind kind,
        string[] args,
        ref int index,
        ref bool verbose,
        ref string? buildConfiguration,
        out ParsedCommand? earlyExit)
    {
        earlyExit = null;
        var token = args[index];

        if (token is "-v" or "--verbose")
        {
            verbose = true;
            return true;
        }

        if (token.Equals("--release", StringComparison.OrdinalIgnoreCase))
        {
            EnsureBuildOrRunConfigurationOption(kind, token);
            buildConfiguration = "Release";
            return true;
        }

        if (token.Equals("--debug", StringComparison.OrdinalIgnoreCase))
        {
            EnsureBuildOrRunConfigurationOption(kind, token);
            buildConfiguration = "Debug";
            return true;
        }

        if (token is "-c" or "--configuration")
        {
            EnsureBuildOrRunConfigurationOption(kind, token);
            index++;
            if (index >= args.Length)
                throw new InvalidOperationException($"Option '{token}' requires a value.");

            buildConfiguration = ParseBuildConfiguration(args[index]);
            return true;
        }

        if (IsHelpToken(token))
        {
            earlyExit = ParsedCommand.Help();
            return true;
        }

        if (IsVersionToken(token))
        {
            earlyExit = ParsedCommand.Version();
            return true;
        }

        return false;
    }

    private static void EnsureBuildOrRunConfigurationOption(CommandKind kind, string option)
    {
        if (kind is CommandKind.Build or CommandKind.Run)
            return;

        throw new InvalidOperationException($"Option '{option}' is only valid for 'build' and 'run'.");
    }

    private static string ParseBuildConfiguration(string value)
    {
        if (value.Equals("debug", StringComparison.OrdinalIgnoreCase))
            return "Debug";
        if (value.Equals("release", StringComparison.OrdinalIgnoreCase))
            return "Release";
        if (value.Equals("relwithdebinfo", StringComparison.OrdinalIgnoreCase))
            return "RelWithDebInfo";
        if (value.Equals("minsizerel", StringComparison.OrdinalIgnoreCase))
            return "MinSizeRel";

        throw new InvalidOperationException(
            $"Unknown configuration '{value}'. Use Debug, Release, RelWithDebInfo, or MinSizeRel.");
    }

    private static bool IsHelpToken(string token) =>
        token.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("--help", StringComparison.OrdinalIgnoreCase);

    private static bool IsVersionToken(string token) =>
        token.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("-V", StringComparison.OrdinalIgnoreCase);

    private static CommandKind ParseCommandKind(string commandText)
    {
        if (commandText.Equals("build", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Build;
        if (commandText.Equals("run", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Run;
        if (commandText.Equals("check", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Check;
        if (commandText.Equals("list", StringComparison.OrdinalIgnoreCase))
            return CommandKind.List;
        if (commandText.Equals("search", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Search;
        if (commandText.Equals("info", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Info;
        if (commandText.Equals("init", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Init;
        if (commandText.Equals("add", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Add;
        if (commandText.Equals("module", StringComparison.OrdinalIgnoreCase) ||
            commandText.Equals("add-module", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Module;
        if (commandText.Equals("help", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Help;
        if (commandText.Equals("version", StringComparison.OrdinalIgnoreCase))
            return CommandKind.Version;

        throw new InvalidOperationException($"Unknown command '{commandText}'.");
    }

    private static string GetVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static List<string> ResolveProjectDirectories(IEnumerable<string> pathArguments)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new HashSet<string>(pathComparer);
        var args = pathArguments.ToList();

        if (args.Count == 0)
        {
            var currentDirectory = Environment.CurrentDirectory;
            if (HasProjectFile(currentDirectory))
            {
                result.Add(currentDirectory);
                return [.. result];
            }

            return [];
        }

        foreach (var arg in args)
        {
            ResolveProjectDirectoryArgument(arg, result);
        }

        return [.. result];
    }

    private static void ResolveProjectDirectoryArgument(string arg, HashSet<string> output)
    {
        var fullPath = Path.GetFullPath(arg);

        if (File.Exists(fullPath))
        {
            TryAddProjectDirectoryFromFile(arg, fullPath, output);
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            Console.Error.WriteLine($"warn: path '{arg}' does not exist - skipping.");
            return;
        }

        if (HasProjectFile(fullPath))
        {
            output.Add(fullPath);
            return;
        }

        var discoveredInChildren = TryAddImmediateChildProjects(fullPath, output);
        if (!discoveredInChildren)
        {
            Console.Error.WriteLine(
                $"warn: directory '{arg}' has no project.json and no immediate child projects - skipping.");
        }
    }

    private static void TryAddProjectDirectoryFromFile(string inputArg, string fullFilePath, HashSet<string> output)
    {
        var fileName = Path.GetFileName(fullFilePath);
        if (!fileName.Equals("project.json", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"warn: '{inputArg}' is a file but not project.json - skipping.");
            return;
        }

        var parentDirectory = Path.GetDirectoryName(fullFilePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
            output.Add(parentDirectory);
    }

    private static bool TryAddImmediateChildProjects(string directoryPath, HashSet<string> output)
    {
        var found = false;
        foreach (var childDirectory in Directory.GetDirectories(directoryPath))
        {
            if (!HasProjectFile(childDirectory))
                continue;

            output.Add(childDirectory);
            found = true;
        }

        return found;
    }

    private static bool HasProjectFile(string directoryPath) =>
        File.Exists(Path.Combine(directoryPath, "project.json"));

    private enum CommandKind
    {
        Help,
        Version,
        Check,
        Build,
        Run,
        List,
        Search,
        Info,
        Init,
        Add,
        Module,
    }

    private sealed record ParsedCommand(
        CommandKind Kind,
        bool Verbose,
        IReadOnlyList<string> Arguments,
        string? BuildConfiguration)
    {
        public static ParsedCommand Help() => new(CommandKind.Help, false, Array.Empty<string>(), null);
        public static ParsedCommand Version() => new(CommandKind.Version, false, Array.Empty<string>(), null);
    }
}
