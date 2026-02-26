using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;
using System.Text.Json;

namespace Abel;

internal static class ProjectLinter
{
    private static readonly string[] ValidExtensions = [".cpp", ".hpp", ".c", ".h", ".cc", ".hh", ".cxx", ".hxx", ".ixx", ".cppm"];
    private static readonly string[] ExcludedDirectories = ["build", "out", "bin", "obj", ".git", ".vs", ".vscode", ".idea", ".abel"];

    public static async Task<bool> TryCheckAsync(IReadOnlyList<string> args, bool verbose, string? buildConfiguration)
    {
        var projectPath = Environment.CurrentDirectory;
        if (args.Count > 0)
        {
            projectPath = Path.GetFullPath(args[0]);
        }
        
        if (!Directory.Exists(projectPath))
        {
            await Console.Error.WriteLineAsync($"error: Directory '{projectPath}' does not exist.").ConfigureAwait(false);
            return false;
        }

        var config = ReadProjectConfig(projectPath);
        if (config is null)
        {
            await Console.Error.WriteLineAsync($"error: Could not read project.json in '{projectPath}'.").ConfigureAwait(false);
            return false;
        }

        var (activeConfiguration, buildDirectory, compileCommandsPath) = ResolveBuildEnvironment(config, buildConfiguration, projectPath);

        if (!File.Exists(compileCommandsPath))
        {
            await Console.Error.WriteLineAsync($"error: Missing '{compileCommandsPath}'. Run 'abel build' first.").ConfigureAwait(false);
            return false;
        }

        var sourceFiles = GetSourceFiles(projectPath);
        if (sourceFiles.Count == 0)
        {
            await Console.Out.WriteLineAsync($"No source files found in '{projectPath}' to analyze.").ConfigureAwait(false);
            return true;
        }

        if (verbose) await Console.Out.WriteLineAsync($"Found {sourceFiles.Count} files. Running clang-tidy...").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Analyzing source files in {activeConfiguration} configuration...").ConfigureAwait(false);
        
        var sw = Stopwatch.StartNew();
        try
        {
            if (!await LintBatchesAsync(BatchFiles(sourceFiles, 20), projectPath, buildDirectory, verbose).ConfigureAwait(false)) return false;
            sw.Stop();
            Console.ForegroundColor = ConsoleColor.Green;
            await Console.Out.WriteLineAsync($"  [ok] static analysis complete ({sw.ElapsedMilliseconds}ms)").ConfigureAwait(false);
            Console.ResetColor();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Console.Error.WriteLineAsync($"error: Could not run clang-tidy: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<bool> LintBatchesAsync(List<List<string>> batches, string projectPath, string buildDirectory, bool verbose)
    {
        var hasErrors = false;
        foreach (var batch in batches)
        {
            var cmdArgs = new List<string> { $"-p={buildDirectory}" };
            cmdArgs.AddRange(batch);
            
            // Allow output streaming directly so users can see warnings/errors as clang-tidy processes them
            var result = await Cli.Wrap("clang-tidy")
                .WithArguments(cmdArgs)
                .WithWorkingDirectory(projectPath)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                .ExecuteAsync();
                
            if (result.ExitCode != 0)
            {
                hasErrors = true;
            }
        }
        
        if (hasErrors)
        {
            await Console.Error.WriteLineAsync("error: clang-tidy reported issues.").ConfigureAwait(false);
            return false;
        }
        
        return true;
    }

    private static List<string> GetSourceFiles(string rootPath)
    {
        var files = new List<string>();
        var directoryQueue = new Queue<string>();
        directoryQueue.Enqueue(rootPath);
        
        while (directoryQueue.Count > 0)
        {
            var currentDir = directoryQueue.Dequeue();
            
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(currentDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if (!ExcludedDirectories.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    {
                        directoryQueue.Enqueue(dir);
                    }
                }
                
                foreach (var file in Directory.EnumerateFiles(currentDir))
                {
                    var ext = Path.GetExtension(file);
                    if (ValidExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }
        
        return files;
    }
    
    private static List<List<string>> BatchFiles(List<string> files, int batchSize)
    {
        var batches = new List<List<string>>();
        for (int i = 0; i < files.Count; i += batchSize)
        {
            batches.Add(files.Skip(i).Take(batchSize).ToList());
        }
        return batches;
    }

    private static Abel.Core.ProjectConfig? ReadProjectConfig(string projectPath)
    {
        var projectFile = Path.Combine(projectPath, "project.json");
        if (!File.Exists(projectFile)) return null;

        try
        {
            var text = File.ReadAllText(projectFile);
            return JsonSerializer.Deserialize<Abel.Core.ProjectConfig>(text);
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveBuildConfiguration(Abel.Core.ProjectConfig config, string? requestedConfig)
    {
        if (!string.IsNullOrWhiteSpace(requestedConfig))
            return NormalizeBuildConfiguration(requestedConfig);

        var configuredValue = config.Build?.DefaultConfiguration;
        if (string.IsNullOrWhiteSpace(configuredValue))
            return "Release";

        return NormalizeBuildConfiguration(configuredValue);
    }

    private static (string ActiveConfig, string BuildDir, string CompileCommands) ResolveBuildEnvironment(Abel.Core.ProjectConfig config, string? buildConfiguration, string projectPath)
    {
        var activeConfiguration = ResolveBuildConfiguration(config, buildConfiguration);
        var buildDirectory = Path.Combine(projectPath, "build", activeConfiguration);
        var compileCommandsPath = Path.Combine(buildDirectory, "compile_commands.json");
        return (activeConfiguration, buildDirectory, compileCommandsPath);
    }

    private static string NormalizeBuildConfiguration(string buildConfiguration)
    {
        if (buildConfiguration.Equals("Debug", StringComparison.OrdinalIgnoreCase)) return "Debug";
        if (buildConfiguration.Equals("Release", StringComparison.OrdinalIgnoreCase)) return "Release";
        if (buildConfiguration.Equals("RelWithDebInfo", StringComparison.OrdinalIgnoreCase)) return "RelWithDebInfo";
        if (buildConfiguration.Equals("MinSizeRel", StringComparison.OrdinalIgnoreCase)) return "MinSizeRel";

        return "Release";
    }
}
