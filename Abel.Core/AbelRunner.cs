using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace Abel.Core;

/*

Abel should one major heading I am having and that is having to know 1 million things when 
building a C++ project. Adding new dependencies takes so much cognitive space in my brain. 
I have to remember so many things its just too much.


Adding new tests, running them, building a project, defining internal dependencies just breaks my mind.

I want to be able to say:
- Abel build
- Abel test
- Abel install (Making a dependency OS wide available)
- Abel run
- "Abel add SDL3 (I already know that this part will be a major headache inducing problem)"

All of these things needs sane default, while there are so many build systems, source generator 
testing frameworks, package managers. 

Abel should just be the sane default for all of them.
- cmake   (build, test)
- doctest (test)
- all package managers suck ass and have the really bad problem of packages that are not updated but wanting to 
use the newest version resulting in building from source regardless. We need a way to automate this, and always
providing working libraries. No clue how we can do that. We probaly shouldnt touch this. We could provide default add for
some libraries and maintain defaults for them by ourselves. Like odin does it.

Able should also have the ability to check for verious things, like installed compilers, which versions are available etc. 
And possible warn the user of possible incompabilities.

*/

public class AbelRunner(bool verbose = false)
{
    private readonly Dictionary<string, ProjectConfig> Projects = [];
    public bool Verbose { get; set; } = verbose;
    /// <summary>
    /// Running a project, by default we are expecting a main.cpp file to exist
    /// next to the project.json file. (We should validate this)
    /// </summary>
    public async Task Run()
    {
        foreach (var project in Projects)
        {
            var projectConfig = project.Value;
            var projectFilePath = project.Key;

            await Build();

            if (projectConfig.ProjectOutputType == OutputType.exe)
            {
                var exePath = Path.Combine(projectFilePath, "build", projectConfig.Name + (OperatingSystem.IsWindows() ? ".exe" : ""));

                Console.WriteLine($"  run {projectConfig.Name}");

                var runSw = Stopwatch.StartNew();

                await Cli.Wrap(exePath)
                    .WithArguments("")
                    .WithWorkingDirectory(Path.Combine(projectFilePath, "build"))
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                runSw.Stop();

                if (Verbose)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  [run] {projectConfig.Name} finished ({FormatElapsed(runSw.Elapsed)})");
                    Console.ResetColor();
                }
            }
        }
    }

    /// <summary>
    /// Building a project
    /// </summary>
    public async Task Build()
    {
        var totalSw = Stopwatch.StartNew();

        foreach (var project in Projects)
        {
            var projectConfig = project.Value;
            var projectFilePath = project.Key;

            Console.WriteLine($"  build {projectConfig.Name}");

            var projectSw = Stopwatch.StartNew();

            try
            {
                var validatedConfig = ValidateProjectFiles(projectFilePath, projectConfig);
                await BuildProject(projectFilePath, validatedConfig).ConfigureAwait(false);
            }
            catch (CliWrap.Exceptions.CommandExecutionException ex)
            {
                if (Verbose)
                    Console.WriteLine(ex.Message);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [retry] {projectConfig.Name} — cleaning and rebuilding...");
                Console.ResetColor();

                CleanBuild(projectFilePath);

                var validatedConfig = ValidateProjectFiles(projectFilePath, projectConfig);
                await BuildProject(projectFilePath, validatedConfig).ConfigureAwait(false);
            }

            projectSw.Stop();

            if (Verbose)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [done] {projectConfig.Name} ({FormatElapsed(projectSw.Elapsed)})");
                Console.ResetColor();
            }
        }

        totalSw.Stop();

        if (Verbose)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  [build] Total: {FormatElapsed(totalSw.Elapsed)}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Running tests
    /// </summary>
    public void Test()
    {

    }

    /// <summary>
    /// Adding a new dependency to a project.
    /// </summary>
    public void Add()
    {

    }

    /// <summary>
    /// We are parsing, all projects found in a folder and adding them to the project list.
    /// </summary>
    public void ParseFolder(string pathToFolder)
    {
        var folderPaths = Directory.GetDirectories(pathToFolder);
        var filesPaths = Directory.GetFiles(pathToFolder);

        List<string> validProjectFilePaths = [];
        // We need to find the root project.json file.

        foreach (var filePath in filesPaths)
        {
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, "project.json", StringComparison.OrdinalIgnoreCase))
                validProjectFilePaths.Add(filePath);
        }


        foreach (var validProjectFilePath in validProjectFilePaths)
        {
            var jsonString = File.ReadAllText(validProjectFilePath);

            if (string.IsNullOrEmpty(jsonString))
                return;

            ProjectConfig? projectConfig = JsonSerializer.Deserialize<ProjectConfig>(jsonString);

            if (projectConfig is not null)
                Projects[Path.GetDirectoryName(validProjectFilePath)!] = projectConfig;
        }
    }

    /// <summary>
    /// Checks that files referenced in the ProjectConfig actually exist on disk.
    /// Missing test files are stripped out and the user is warned via console.
    /// Returns a new config safe to pass to CmakeBuilder.
    /// </summary>
    private ProjectConfig ValidateProjectFiles(string projectDir, ProjectConfig config)
    {
        // Validate test files — remove missing ones so the CMake script stays valid.
        var validTestFiles = new List<string>();

        foreach (var testFile in config.Tests.Files)
        {
            var fullPath = Path.Combine(projectDir, testFile);
            if (File.Exists(fullPath))
            {
                validTestFiles.Add(testFile);
            }
            else
            {
                if (!Verbose)
                    break;

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [warn] Test file '{testFile}' not found in {projectDir} — skipping.");
                Console.ResetColor();
            }
        }

        // Only create a modified copy if something was actually removed.
        if (validTestFiles.Count == config.Tests.Files.Count)
            return config;

        if (validTestFiles.Count == 0 && config.Tests.Files.Count > 0 && Verbose)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  [warn] No test files found for '{config.Name}'. Building without tests.");
            Console.ResetColor();
        }

        // Shallow copy with the filtered test list.
        // We don't modify the original config — ParseFolder owns that.
        var validated = new ProjectConfig
        {
            Name = config.Name,
            CXXStandard = config.CXXStandard,
            ProjectOutputType = config.ProjectOutputType,
            Tests = new TestsConfig { Files = validTestFiles },
        };

        foreach (var dep in config.Dependencies)
            validated.Dependencies.Add(dep);

        foreach (var kvp in config.Sources)
            validated.Sources[kvp.Key] = kvp.Value;

        return validated;
    }

    private async Task BuildProject(string projectFilePath, ProjectConfig projectConfig)
    {
        var cmakeScript = CmakeBuilder.FromProjectConfig(projectConfig).Build();

        await File.WriteAllTextAsync(Path.Combine(projectFilePath, "CMakeLists.txt"), cmakeScript);

        if (Verbose)
        {
            await Cli.Wrap("cmake")
                    .WithArguments("-S . -B build -G Ninja")
                    .WithWorkingDirectory(projectFilePath)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();

            await Cli.Wrap("cmake")
                    .WithArguments("--build build")
                    .WithWorkingDirectory(projectFilePath)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
        }
        else
        {
            await Cli.Wrap("cmake")
                    .WithArguments("-S . -B build -G Ninja")
                    .WithWorkingDirectory(projectFilePath)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();

            await Cli.Wrap("cmake")
                    .WithArguments("--build build")
                    .WithWorkingDirectory(projectFilePath)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
        }
    }

    /// <summary>
    /// Formats a TimeSpan into a human-friendly string.
    /// Under 1s: "123ms", under 1min: "4.56s", otherwise: "1m 23s".
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1000)
            return $"{elapsed.TotalMilliseconds:F0}ms";
        if (elapsed.TotalSeconds < 60)
            return $"{elapsed.TotalSeconds:F2}s";
        return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
    }

    // When a build fails its a good chance that by doing a clean build once it works.
    private static void CleanBuild(string projectFilePath)
    {
        Directory.Delete(Path.Combine(projectFilePath, "build"), recursive: true);
    }

}