using System.Diagnostics;
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
    private static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, ProjectConfig> Projects = new(PathComparer);
    private readonly PackageRegistry _registry = new();

    private sealed record LocalProjectReference(string DirectoryPath, ProjectConfig Config);

    public bool Verbose { get; set; } = verbose;

    /// <summary>
    /// Running a project, by default we are expecting a main.cpp file to exist
    /// next to the project.json file. (We should validate this)
    /// </summary>
    public async Task Run()
    {
        await Build().ConfigureAwait(false);

        foreach (var project in Projects)
        {
            var projectConfig = project.Value;
            var projectFilePath = project.Key;

            if (projectConfig.ProjectOutputType == OutputType.exe)
            {
                var exePath = Path.Combine(
                    projectFilePath,
                    "build",
                    projectConfig.Name + (OperatingSystem.IsWindows() ? ".exe" : ""));

                Console.WriteLine($"  run {projectConfig.Name}");

                var runSw = Stopwatch.StartNew();

                await Cli.Wrap(exePath)
                    .WithArguments(Array.Empty<string>())
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
        var alreadyBuilt = new HashSet<string>(PathComparer);

        foreach (var project in Projects)
        {
            var projectFilePath = project.Key;
            var projectConfig = project.Value;
            var localProjectIndex = BuildLocalProjectIndex(projectFilePath);
            var localInstallPrefix = Path.Combine(projectFilePath, ".abel", "local_deps");

            Directory.CreateDirectory(localInstallPrefix);

            var validatedConfig = ValidateProjectFiles(projectFilePath, projectConfig);

            await BuildProjectWithDependencies(
                projectFilePath,
                validatedConfig,
                localProjectIndex,
                localInstallPrefix,
                activeBuildStack: new HashSet<string>(PathComparer),
                alreadyBuilt: alreadyBuilt
            ).ConfigureAwait(false);
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
    /// We are parsing the root project in a folder and adding it to the project list.
    /// </summary>
    public void ParseFolder(string pathToFolder)
    {
        var projectFilePath = Path.Combine(pathToFolder, "project.json");
        if (!File.Exists(projectFilePath))
            return;

        var jsonString = File.ReadAllText(projectFilePath);

        if (string.IsNullOrWhiteSpace(jsonString))
            return;

        var projectConfig = JsonSerializer.Deserialize<ProjectConfig>(jsonString);

        if (projectConfig is not null)
            Projects[pathToFolder] = projectConfig;
    }

    /// <summary>
    /// Checks that files referenced in the ProjectConfig actually exist on disk.
    /// Missing test files are stripped out and the user is warned via console.
    /// Returns a new config safe to pass to CmakeBuilder.
    /// </summary>
    private ProjectConfig ValidateProjectFiles(string projectDir, ProjectConfig config)
    {
        // Validate test files - remove missing ones so the CMake script stays valid.
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
                Console.WriteLine($"  [warn] Test file '{testFile}' not found in {projectDir} - skipping.");
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
        // We don't modify the original config - ParseFolder owns that.
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

    private async Task BuildProjectWithDependencies(
        string projectFilePath,
        ProjectConfig projectConfig,
        IReadOnlyDictionary<string, LocalProjectReference> localProjectIndex,
        string localInstallPrefix,
        ISet<string> activeBuildStack,
        ISet<string> alreadyBuilt)
    {
        if (alreadyBuilt.Contains(projectFilePath))
            return;

        if (!activeBuildStack.Add(projectFilePath))
        {
            throw new InvalidOperationException(
                $"Circular dependency detected while building '{projectConfig.Name}' in '{projectFilePath}'.");
        }

        try
        {
            var localDependencies = ResolveLocalDependencies(
                projectFilePath,
                projectConfig,
                localProjectIndex);

            foreach (var localDependency in localDependencies.Values)
            {
                if (localDependency.Config.ProjectOutputType != OutputType.library)
                {
                    throw new InvalidOperationException(
                        $"Dependency '{localDependency.Config.Name}' is not a library. " +
                        $"Only library dependencies are supported.");
                }

                var validatedDependencyConfig = ValidateProjectFiles(
                    localDependency.DirectoryPath,
                    localDependency.Config);

                await BuildProjectWithDependencies(
                    localDependency.DirectoryPath,
                    validatedDependencyConfig,
                    localProjectIndex,
                    localInstallPrefix,
                    activeBuildStack,
                    alreadyBuilt
                ).ConfigureAwait(false);
            }

            Console.WriteLine($"  build {projectConfig.Name}");
            var projectSw = Stopwatch.StartNew();

            try
            {
                await BuildProject(projectFilePath, projectConfig, localInstallPrefix).ConfigureAwait(false);

                if (projectConfig.ProjectOutputType == OutputType.library)
                    await InstallProject(projectFilePath, localInstallPrefix).ConfigureAwait(false);
            }
            catch (CliWrap.Exceptions.CommandExecutionException ex)
            {
                if (Verbose)
                    Console.WriteLine(ex.Message);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [retry] {projectConfig.Name} - cleaning and rebuilding...");
                Console.ResetColor();

                CleanBuild(projectFilePath);

                await BuildProject(projectFilePath, projectConfig, localInstallPrefix).ConfigureAwait(false);

                if (projectConfig.ProjectOutputType == OutputType.library)
                    await InstallProject(projectFilePath, localInstallPrefix).ConfigureAwait(false);
            }

            projectSw.Stop();
            alreadyBuilt.Add(projectFilePath);

            if (Verbose)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [done] {projectConfig.Name} ({FormatElapsed(projectSw.Elapsed)})");
                Console.ResetColor();
            }
        }
        finally
        {
            activeBuildStack.Remove(projectFilePath);
        }
    }

    private async Task BuildProject(
        string projectFilePath,
        ProjectConfig projectConfig,
        string localInstallPrefix)
    {
        var cmakeScript = CmakeBuilder.FromProjectConfig(projectConfig, _registry).Build();
        var cmakeListsPath = Path.Combine(projectFilePath, "CMakeLists.txt");
        var cmakeListsChanged = await WriteTextIfChanged(cmakeListsPath, cmakeScript).ConfigureAwait(false);

        var configureArguments = new[]
        {
            "-S", ".",
            "-B", "build",
            "-G", "Ninja",
            $"-DCMAKE_PREFIX_PATH={localInstallPrefix}"
        };

        var buildCachePath = Path.Combine(projectFilePath, "build", "CMakeCache.txt");
        var needsConfigure = cmakeListsChanged || !File.Exists(buildCachePath);

        if (Verbose)
        {
            if (needsConfigure)
            {
                await Cli.Wrap("cmake")
                    .WithArguments(configureArguments)
                    .WithWorkingDirectory(projectFilePath)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                    .ExecuteBufferedAsync().ConfigureAwait(false);
            }

            await Cli.Wrap("cmake")
                .WithArguments(["--build", "build"])
                .WithWorkingDirectory(projectFilePath)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                .ExecuteBufferedAsync().ConfigureAwait(false);
        }
        else
        {
            if (needsConfigure)
            {
                await Cli.Wrap("cmake")
                    .WithArguments(configureArguments)
                    .WithWorkingDirectory(projectFilePath)
                    .ExecuteBufferedAsync().ConfigureAwait(false);
            }

            await Cli.Wrap("cmake")
                .WithArguments(["--build", "build"])
                .WithWorkingDirectory(projectFilePath)
                .ExecuteBufferedAsync().ConfigureAwait(false);
        }
    }

    private async Task InstallProject(string projectFilePath, string localInstallPrefix)
    {
        if (Verbose)
        {
            await Cli.Wrap("cmake")
                .WithArguments(["--install", "build", "--prefix", localInstallPrefix])
                .WithWorkingDirectory(projectFilePath)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                .ExecuteBufferedAsync().ConfigureAwait(false);
        }
        else
        {
            await Cli.Wrap("cmake")
                .WithArguments(["--install", "build", "--prefix", localInstallPrefix])
                .WithWorkingDirectory(projectFilePath)
                .ExecuteBufferedAsync().ConfigureAwait(false);
        }
    }

    private IReadOnlyDictionary<string, LocalProjectReference> ResolveLocalDependencies(
        string projectFilePath,
        ProjectConfig projectConfig,
        IReadOnlyDictionary<string, LocalProjectReference> localProjectIndex)
    {
        var localDependencies = new Dictionary<string, LocalProjectReference>(NameComparer);

        foreach (var dependencyText in projectConfig.Dependencies)
        {
            var dependencySpec = DependencySpec.Parse(dependencyText);

            if (_registry.IsKnownPackage(dependencySpec.PackageName))
                continue;

            if (dependencySpec.VariantName is not null)
            {
                throw new InvalidOperationException(
                    $"Dependency '{dependencyText}' uses variant syntax but '{dependencySpec.PackageName}' " +
                    "is not a known registry package.");
            }

            if (!localProjectIndex.TryGetValue(dependencySpec.PackageName, out var localDependency))
                continue;

            if (PathComparer.Equals(localDependency.DirectoryPath, projectFilePath))
                continue;

            localDependencies[dependencySpec.PackageName] = localDependency;
        }

        return localDependencies;
    }

    private static Dictionary<string, LocalProjectReference> BuildLocalProjectIndex(string rootProjectPath)
    {
        var localProjects = new Dictionary<string, LocalProjectReference>(NameComparer);

        foreach (var projectFilePath in Directory.EnumerateFiles(
                     rootProjectPath,
                     "project.json",
                     SearchOption.AllDirectories))
        {
            var projectDirectoryPath = Path.GetDirectoryName(projectFilePath);
            if (projectDirectoryPath is null || ShouldIgnoreDiscoveredProject(projectDirectoryPath))
                continue;

            var jsonString = File.ReadAllText(projectFilePath);
            if (string.IsNullOrWhiteSpace(jsonString))
                continue;

            var projectConfig = JsonSerializer.Deserialize<ProjectConfig>(jsonString);
            if (projectConfig is null)
                continue;

            if (localProjects.TryGetValue(projectConfig.Name, out var existing) &&
                !PathComparer.Equals(existing.DirectoryPath, projectDirectoryPath))
            {
                throw new InvalidOperationException(
                    $"Found more than one nested project named '{projectConfig.Name}' under '{rootProjectPath}'. " +
                    $"Ambiguous paths: '{existing.DirectoryPath}' and '{projectDirectoryPath}'.");
            }

            localProjects[projectConfig.Name] = new LocalProjectReference(projectDirectoryPath, projectConfig);
        }

        return localProjects;
    }

    private static bool ShouldIgnoreDiscoveredProject(string projectDirectoryPath)
    {
        var segments = projectDirectoryPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment.Equals("build", StringComparison.OrdinalIgnoreCase))
                return true;

            if (segment.Equals(".abel", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
        var buildPath = Path.Combine(projectFilePath, "build");
        if (Directory.Exists(buildPath))
            Directory.Delete(buildPath, recursive: true);
    }

    private static async Task<bool> WriteTextIfChanged(string path, string content)
    {
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (string.Equals(existing, content, StringComparison.Ordinal))
                return false;
        }

        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
        return true;
    }
}
