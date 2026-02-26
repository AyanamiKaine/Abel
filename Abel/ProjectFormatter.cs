using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

namespace Abel;

internal static class ProjectFormatter
{
    private static readonly string[] ValidExtensions = [".cpp", ".hpp", ".c", ".h", ".cc", ".hh", ".cxx", ".hxx", ".ixx", ".cppm"];
    private static readonly string[] ExcludedDirectories = ["build", "out", "bin", "obj", ".git", ".vs", ".vscode", ".idea", ".abel"];

    public static async Task<bool> TryFormatAsync(IReadOnlyList<string> args, bool verbose)
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

        var sourceFiles = GetSourceFiles(projectPath);

        if (sourceFiles.Count == 0)
        {
            await Console.Out.WriteLineAsync($"No source files found in '{projectPath}' to format.").ConfigureAwait(false);
            return true;
        }

        if (verbose)
        {
            await Console.Out.WriteLineAsync($"Found {sourceFiles.Count} files to format.").ConfigureAwait(false);
        }

        await Console.Out.WriteLineAsync("Formatting source files...").ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        
        try
        {
            var batches = BatchFiles(sourceFiles, 50);
            
            if (!await FormatBatchesAsync(batches, projectPath, verbose).ConfigureAwait(false))
            {
                return false;
            }
            
            sw.Stop();
            Console.ForegroundColor = ConsoleColor.Green;
            await Console.Out.WriteLineAsync($"  [ok] formatting complete ({sw.ElapsedMilliseconds}ms)").ConfigureAwait(false);
            Console.ResetColor();
            
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Console.Error.WriteLineAsync($"error: Could not run clang-format: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<bool> FormatBatchesAsync(List<List<string>> batches, string projectPath, bool verbose)
    {
        foreach (var batch in batches)
        {
            var cmdArgs = new List<string> { "-style=file", "-fallback-style=LLVM", "-i" };
            cmdArgs.AddRange(batch);
            
            var result = await Cli.Wrap("clang-format")
                .WithArguments(cmdArgs)
                .WithWorkingDirectory(projectPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
                
            if (result.ExitCode != 0)
            {
                await Console.Error.WriteLineAsync("error: clang-format failed.").ConfigureAwait(false);
                if (verbose)
                {
                    await Console.Error.WriteLineAsync(result.StandardError).ConfigureAwait(false);
                }
                return false;
            }
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
}
