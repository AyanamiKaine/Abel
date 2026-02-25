using System.Diagnostics;
using System.Text.Json;
using CliWrap;

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

public sealed class AbelRunner(bool verbose = false, string? buildConfiguration = null) : IDisposable
{
    private static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, ProjectConfig> Projects = new(PathComparer);
    private readonly Dictionary<string, GitDependencyReference> _gitDependencyCache = new(NameComparer);
    private readonly PackageRegistry _registry = new();
    private readonly ChildProcessScope _childProcessScope = new();
    private readonly string? _requestedBuildConfiguration = NormalizeBuildConfigurationOrNull(buildConfiguration);
    private bool _lastFailureIsCompilationError;
    private string? _lastFailedActivity;
    private bool _disposed;

    private sealed record LocalProjectReference(string DirectoryPath, ProjectConfig Config);
    private sealed record GitDependencyReference(string Repository, string? GitTag, LocalProjectReference Project);

    public bool Verbose { get; set; } = verbose;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
            _childProcessScope.Dispose();

        _disposed = true;
    }

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
                var effectiveBuildConfiguration = ResolveBuildConfiguration(projectConfig);
                var exePath = ResolveExecutablePath(projectFilePath, projectConfig.Name, effectiveBuildConfiguration);
                var exeDirectory = Path.GetDirectoryName(exePath) ??
                                   Path.Combine(projectFilePath, GetBuildDirectoryPath(effectiveBuildConfiguration));

                Console.WriteLine($"  run {projectConfig.Name} ({effectiveBuildConfiguration})");

                var runSw = Stopwatch.StartNew();

                var runCommand = Cli.Wrap(exePath)
                    .WithArguments(Array.Empty<string>())
                    .WithWorkingDirectory(exeDirectory)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                    .WithValidation(CommandResultValidation.None);

                await ExecuteManagedAsync(runCommand).ConfigureAwait(false);

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
            _gitDependencyCache.Clear();

            var projectFilePath = project.Key;
            var projectConfig = project.Value;
            var localProjectIndex = BuildLocalProjectIndex(projectFilePath);
            var localInstallPrefix = Path.Combine(projectFilePath, ".abel", "local_deps");

            Directory.CreateDirectory(localInstallPrefix);

            var validatedConfig = ValidateProjectFiles(projectFilePath, projectConfig);
            var effectiveBuildConfiguration = ResolveBuildConfiguration(validatedConfig);

            await BuildProjectWithDependencies(
                projectFilePath,
                validatedConfig,
                effectiveBuildConfiguration,
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
            Build = config.Build,
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
        string buildConfiguration,
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
            var gitDependencies = await ResolveGitDependencies(projectFilePath, projectConfig, localInstallPrefix).ConfigureAwait(false);
            MergeDependencies(localDependencies, gitDependencies);

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
                    buildConfiguration,
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
                await BuildProject(projectFilePath, projectConfig, localInstallPrefix, buildConfiguration).ConfigureAwait(false);

                if (projectConfig.ProjectOutputType == OutputType.library)
                    await InstallProject(projectFilePath, localInstallPrefix, projectConfig.Name, buildConfiguration).ConfigureAwait(false);
            }
            catch (CliWrap.Exceptions.CommandExecutionException ex)
            {
                if (Verbose)
                    Console.WriteLine(ex.Message);

                if (!ShouldRetryAfterFailure())
                    throw;

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [retry] {projectConfig.Name} - cleaning and rebuilding...");
                Console.ResetColor();

                CleanBuild(projectFilePath, buildConfiguration);

                await BuildProject(projectFilePath, projectConfig, localInstallPrefix, buildConfiguration).ConfigureAwait(false);

                if (projectConfig.ProjectOutputType == OutputType.library)
                    await InstallProject(projectFilePath, localInstallPrefix, projectConfig.Name, buildConfiguration).ConfigureAwait(false);
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
        string localInstallPrefix,
        string buildConfiguration)
    {
        var buildDirectory = GetBuildDirectoryPath(buildConfiguration);

        var registryDependencyPlan = BuildRegistryDependencyPlan(projectConfig);
        if (registryDependencyPlan.Count > 0)
            WriteProgressLine($"  step {projectConfig.Name}: fetch/build dependencies {FormatDependencyList(registryDependencyPlan)}");

        WriteProgressLine($"  step {projectConfig.Name}: generate CMakeLists.txt");
        var cmakeScript = CmakeBuilder.FromProjectConfig(projectConfig, _registry).Build();
        var cmakeListsPath = Path.Combine(projectFilePath, "CMakeLists.txt");
        var cmakeListsChanged = await WriteTextIfChanged(cmakeListsPath, cmakeScript).ConfigureAwait(false);
        WriteProgressLine(cmakeListsChanged
            ? $"  [ok] CMakeLists.txt updated for {projectConfig.Name}"
            : $"  [ok] CMakeLists.txt unchanged for {projectConfig.Name}");

        var configureArguments = new[]
        {
            "-S", ".",
            "-B", buildDirectory,
            "-G", "Ninja",
            $"-DCMAKE_BUILD_TYPE={buildConfiguration}",
            "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON",
            $"-DCMAKE_PREFIX_PATH={localInstallPrefix}"
        };

        var buildCachePath = Path.Combine(projectFilePath, buildDirectory, "CMakeCache.txt");
        var needsConfigure = cmakeListsChanged || !IsConfigureUpToDate(buildCachePath, buildConfiguration);

        if (needsConfigure)
        {
            await ExecuteCommandAsync(
                "cmake",
                configureArguments,
                projectFilePath,
                $"configure {projectConfig.Name}",
                ParseConfigureProgress).ConfigureAwait(false);
        }
        else
        {
            WriteProgressLine($"  [ok] configure {projectConfig.Name} (up-to-date)");
        }

        await ExecuteCommandAsync(
            "cmake",
            ["--build", buildDirectory, "--config", buildConfiguration],
            projectFilePath,
            $"build {projectConfig.Name}",
            ParseBuildProgress).ConfigureAwait(false);
    }

    private async Task InstallProject(string projectFilePath, string localInstallPrefix, string projectName, string buildConfiguration)
    {
        var buildDirectory = GetBuildDirectoryPath(buildConfiguration);

        await ExecuteCommandAsync(
            "cmake",
            ["--install", buildDirectory, "--config", buildConfiguration, "--prefix", localInstallPrefix],
            projectFilePath,
            $"install {projectName}",
            ParseInstallProgress).ConfigureAwait(false);
    }

    private Dictionary<string, LocalProjectReference> ResolveLocalDependencies(
        string projectFilePath,
        ProjectConfig projectConfig,
        IReadOnlyDictionary<string, LocalProjectReference> localProjectIndex)
    {
        var localDependencies = new Dictionary<string, LocalProjectReference>(NameComparer);

        foreach (var dependencyText in projectConfig.Dependencies)
        {
            var dependencySpec = ProjectDependencySpec.Parse(dependencyText);

            if (dependencySpec.IsGit)
                continue;

            if (_registry.IsKnownPackage(dependencySpec.Name))
                continue;

            if (dependencySpec.VariantName is not null)
            {
                throw new InvalidOperationException(
                    $"Dependency '{dependencyText}' uses variant syntax but '{dependencySpec.Name}' " +
                    "is not a known registry package.");
            }

            if (!localProjectIndex.TryGetValue(dependencySpec.Name, out var localDependency))
                continue;

            if (PathComparer.Equals(localDependency.DirectoryPath, projectFilePath))
                continue;

            localDependencies[dependencySpec.Name] = localDependency;
        }

        return localDependencies;
    }

    private async Task<IReadOnlyDictionary<string, LocalProjectReference>> ResolveGitDependencies(
        string projectFilePath,
        ProjectConfig projectConfig,
        string localInstallPrefix)
    {
        var gitDependencies = new Dictionary<string, LocalProjectReference>(NameComparer);

        foreach (var dependencyText in projectConfig.Dependencies)
        {
            var dependencySpec = ProjectDependencySpec.Parse(dependencyText);
            if (!dependencySpec.IsGit)
                continue;

            var dependencyReference = await ResolveGitDependency(dependencySpec, localInstallPrefix).ConfigureAwait(false);
            if (PathComparer.Equals(dependencyReference.DirectoryPath, projectFilePath))
                continue;

            gitDependencies[dependencySpec.Name] = dependencyReference;
        }

        return gitDependencies;
    }

    private async Task<LocalProjectReference> ResolveGitDependency(
        ProjectDependencySpec dependencySpec,
        string localInstallPrefix)
    {
        if (_gitDependencyCache.TryGetValue(dependencySpec.Name, out var cached))
        {
            EnsureGitDependencyMatchesCachedSource(dependencySpec, cached);
            return cached.Project;
        }

        var dependencyDirectoryPath = GetGitDependencyDirectory(localInstallPrefix, dependencySpec.Name);
        await EnsureGitRepositoryAvailable(dependencySpec, dependencyDirectoryPath).ConfigureAwait(false);

        var projectFilePath = Path.Combine(dependencyDirectoryPath, "project.json");
        if (!File.Exists(projectFilePath))
        {
            throw new InvalidOperationException(
                $"Git dependency '{dependencySpec.Name}' from '{dependencySpec.GitRepository}' does not contain project.json at the repository root.");
        }

        var json = await File.ReadAllTextAsync(projectFilePath).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<ProjectConfig>(json)
                     ?? throw new InvalidOperationException($"Could not parse '{projectFilePath}'.");

        if (!config.Name.Equals(dependencySpec.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Git dependency '{dependencySpec.Name}' points to project '{config.Name}'. " +
                "Dependency name must match project.json name.");
        }

        var projectReference = new LocalProjectReference(dependencyDirectoryPath, config);
        _gitDependencyCache[dependencySpec.Name] =
            new GitDependencyReference(dependencySpec.GitRepository!, dependencySpec.GitTag, projectReference);

        return projectReference;
    }

    private static void EnsureGitDependencyMatchesCachedSource(
        ProjectDependencySpec dependencySpec,
        GitDependencyReference cached)
    {
        var sameRepository = string.Equals(cached.Repository, dependencySpec.GitRepository, StringComparison.OrdinalIgnoreCase);
        var sameTag = string.Equals(cached.GitTag, dependencySpec.GitTag, StringComparison.OrdinalIgnoreCase);

        if (sameRepository && sameTag)
            return;

        throw new InvalidOperationException(
            $"Dependency '{dependencySpec.Name}' was referenced multiple times with different git sources. " +
            $"First: '{cached.Repository}{FormatGitTag(cached.GitTag)}'. " +
            $"Later: '{dependencySpec.GitRepository}{FormatGitTag(dependencySpec.GitTag)}'.");
    }

    private async Task EnsureGitRepositoryAvailable(
        ProjectDependencySpec dependencySpec,
        string dependencyDirectoryPath)
    {
        var gitDirectory = Path.Combine(dependencyDirectoryPath, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            if (Directory.Exists(dependencyDirectoryPath) &&
                Directory.EnumerateFileSystemEntries(dependencyDirectoryPath).Any())
            {
                throw new InvalidOperationException(
                    $"Git dependency cache path '{dependencyDirectoryPath}' exists and is not a git repository.");
            }

            var cacheRoot = Path.GetDirectoryName(dependencyDirectoryPath);
            if (!string.IsNullOrWhiteSpace(cacheRoot))
                Directory.CreateDirectory(cacheRoot);

            await ExecuteCommandAsync(
                    "git",
                    ["clone", "--recursive", dependencySpec.GitRepository!, dependencyDirectoryPath],
                    Environment.CurrentDirectory,
                    $"fetch git dependency {dependencySpec.Name}")
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(dependencySpec.GitTag))
            return;

        await ExecuteCommandAsync(
                "git",
                ["-C", dependencyDirectoryPath, "checkout", dependencySpec.GitTag],
                Environment.CurrentDirectory,
                $"checkout git dependency {dependencySpec.Name}{FormatGitTag(dependencySpec.GitTag)}")
            .ConfigureAwait(false);
    }

    private static string GetGitDependencyDirectory(string localInstallPrefix, string dependencyName)
    {
        var abelDirectory = Path.GetDirectoryName(localInstallPrefix) ?? localInstallPrefix;
        var gitDependencyRoot = Path.Combine(abelDirectory, "git_deps");
        Directory.CreateDirectory(gitDependencyRoot);
        return Path.Combine(gitDependencyRoot, ToSafeDependencyDirectoryName(dependencyName));
    }

    private static string ToSafeDependencyDirectoryName(string dependencyName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeChars = dependencyName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();

        var safeName = new string(safeChars).Trim();
        return string.IsNullOrWhiteSpace(safeName) ? "git_dependency" : safeName;
    }

    private static void MergeDependencies(
        Dictionary<string, LocalProjectReference> localDependencies,
        IReadOnlyDictionary<string, LocalProjectReference> additionalDependencies)
    {
        foreach (var dependency in additionalDependencies)
        {
            if (localDependencies.TryGetValue(dependency.Key, out var existing) &&
                !PathComparer.Equals(existing.DirectoryPath, dependency.Value.DirectoryPath))
            {
                throw new InvalidOperationException(
                    $"Dependency '{dependency.Key}' resolves to multiple paths: " +
                    $"'{existing.DirectoryPath}' and '{dependency.Value.DirectoryPath}'.");
            }

            localDependencies[dependency.Key] = dependency.Value;
        }
    }

    private static string FormatGitTag(string? gitTag) =>
        string.IsNullOrWhiteSpace(gitTag) ? "" : $"#{gitTag}";

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
    private static void CleanBuild(string projectFilePath, string buildConfiguration)
    {
        var buildPath = Path.Combine(projectFilePath, GetBuildDirectoryPath(buildConfiguration));
        if (Directory.Exists(buildPath))
            Directory.Delete(buildPath, recursive: true);
    }

    private static string GetBuildDirectoryPath(string buildConfiguration) =>
        Path.Combine("build", buildConfiguration);

    private static bool IsConfigureUpToDate(string buildCachePath, string expectedBuildConfiguration)
    {
        if (!File.Exists(buildCachePath))
            return false;

        const string buildTypePrefix = "CMAKE_BUILD_TYPE:STRING=";
        foreach (var line in File.ReadLines(buildCachePath))
        {
            if (!line.StartsWith(buildTypePrefix, StringComparison.Ordinal))
                continue;

            var actualBuildType = line[buildTypePrefix.Length..].Trim();
            return string.Equals(actualBuildType, expectedBuildConfiguration, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool ShouldRetryAfterFailure()
    {
        if (_lastFailureIsCompilationError)
            return false;

        if (string.IsNullOrWhiteSpace(_lastFailedActivity))
            return true;

        return !_lastFailedActivity.StartsWith("build ", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveBuildConfiguration(ProjectConfig projectConfig)
    {
        if (_requestedBuildConfiguration is not null)
            return _requestedBuildConfiguration;

        var configuredValue = projectConfig.Build?.DefaultConfiguration;
        if (string.IsNullOrWhiteSpace(configuredValue))
            return "Release";

        try
        {
            return NormalizeBuildConfiguration(configuredValue);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Project '{projectConfig.Name}' has unsupported build.default_configuration '{configuredValue}'. " +
                "Use Debug, Release, RelWithDebInfo, or MinSizeRel.");
        }
    }

    private string ResolveExecutablePath(string projectFilePath, string projectName, string buildConfiguration)
    {
        var executableName = projectName + (OperatingSystem.IsWindows() ? ".exe" : "");
        var configScopedPath = Path.Combine(projectFilePath, GetBuildDirectoryPath(buildConfiguration), executableName);
        if (File.Exists(configScopedPath))
            return configScopedPath;

        // Backward compatibility for projects built before config-specific folders.
        var legacyPath = Path.Combine(projectFilePath, "build", executableName);
        if (File.Exists(legacyPath))
            return legacyPath;

        return configScopedPath;
    }

    private static string? NormalizeBuildConfigurationOrNull(string? buildConfiguration)
    {
        if (string.IsNullOrWhiteSpace(buildConfiguration))
            return null;

        return NormalizeBuildConfiguration(buildConfiguration);
    }

    private static string NormalizeBuildConfiguration(string buildConfiguration)
    {
        if (buildConfiguration.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            return "Debug";
        if (buildConfiguration.Equals("Release", StringComparison.OrdinalIgnoreCase))
            return "Release";
        if (buildConfiguration.Equals("RelWithDebInfo", StringComparison.OrdinalIgnoreCase))
            return "RelWithDebInfo";
        if (buildConfiguration.Equals("MinSizeRel", StringComparison.OrdinalIgnoreCase))
            return "MinSizeRel";

        throw new InvalidOperationException(
            $"Unsupported configuration '{buildConfiguration}'. Use Debug, Release, RelWithDebInfo, or MinSizeRel.");
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

    private async Task ExecuteCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string activityLabel,
        Func<string, string?>? progressParser = null)
    {
        _lastFailureIsCompilationError = false;
        _lastFailedActivity = null;

        if (Verbose)
        {
            var verboseCommand = Cli.Wrap(fileName)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(Console.WriteLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine));

            try
            {
                await ExecuteManagedAsync(verboseCommand).ConfigureAwait(false);
            }
            catch (CliWrap.Exceptions.CommandExecutionException)
            {
                _lastFailedActivity = activityLabel;
                throw;
            }

            return;
        }

        var activity = new ActivityState();
        var outputBuffer = new CommandOutputBuffer();
        var outputPipe = CreateActivityPipe(
            progressParser,
            activity,
            emitDetails: Console.IsOutputRedirected,
            outputBuffer);

        var command = Cli.Wrap(fileName)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithStandardOutputPipe(outputPipe)
            .WithStandardErrorPipe(outputPipe);

        try
        {
            if (Console.IsOutputRedirected)
            {
                Console.WriteLine($"  {activityLabel}...");
                await ExecuteManagedAsync(command).ConfigureAwait(false);
                return;
            }

            await RunWithSpinnerAsync(activityLabel, activity, command).ConfigureAwait(false);
        }
        catch (CliWrap.Exceptions.CommandExecutionException)
        {
            _lastFailedActivity = activityLabel;
            _lastFailureIsCompilationError = outputBuffer.HasCompilationError();
            PrintFailureDiagnostics(activityLabel, outputBuffer);

            throw;
        }
    }

    private static PipeTarget CreateActivityPipe(
        Func<string, string?>? progressParser,
        ActivityState activity,
        bool emitDetails,
        CommandOutputBuffer? outputBuffer = null)
    {
        if (progressParser is null && outputBuffer is null)
            return PipeTarget.Null;

        return PipeTarget.ToDelegate(line =>
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            outputBuffer?.Add(line);
            if (progressParser is null)
                return;

            var detail = progressParser(line);
            if (!activity.TrySet(detail))
                return;

            if (!emitDetails || string.IsNullOrWhiteSpace(detail))
                return;

            Console.WriteLine($"    > {detail}");
        });
    }

    private static void PrintFailureDiagnostics(string activityLabel, CommandOutputBuffer outputBuffer)
    {
        var diagnostics = outputBuffer.GetDiagnostics();
        if (diagnostics.Count == 0)
            return;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  diagnostics {activityLabel}:");
        Console.ResetColor();

        foreach (var line in diagnostics)
            Console.WriteLine($"    {line}");
    }

    private async Task RunWithSpinnerAsync(string activityLabel, ActivityState activity, Command command)
    {
        var timer = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        var spinnerTask = SpinAsync(activityLabel, activity, timer, cts.Token);

        try
        {
            await ExecuteManagedAsync(command).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            await WaitForSpinnerStop(spinnerTask).ConfigureAwait(false);

            WriteCompletedLine($"  [ok] {activityLabel} ({FormatElapsed(timer.Elapsed)})");
        }
        catch
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await WaitForSpinnerStop(spinnerTask).ConfigureAwait(false);

            WriteCompletedLine($"  [fail] {activityLabel} ({FormatElapsed(timer.Elapsed)})");
            throw;
        }
    }

    private async Task ExecuteManagedAsync(Command command)
    {
        await command.ExecuteAsync(
                configureStartInfo: _ => { },
                configureProcess: process => _childProcessScope.TryAttach(process),
                forcefulCancellationToken: CancellationToken.None,
                gracefulCancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task SpinAsync(string activityLabel, ActivityState activity, Stopwatch timer, CancellationToken token)
    {
        string[] frames = ["|", "/", "-", "\\"];
        var frameIndex = 0;

        while (!token.IsCancellationRequested)
        {
            var frame = frames[frameIndex % frames.Length];
            frameIndex++;

            var detail = activity.Get();
            var label = string.IsNullOrWhiteSpace(detail)
                ? activityLabel
                : $"{activityLabel} | {detail}";

            WriteStatusLine($"  [{frame}] {label} {FormatElapsed(timer.Elapsed)}");

            try
            {
                await Task.Delay(120, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task WaitForSpinnerStop(Task spinnerTask)
    {
        try
        {
            await spinnerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void WriteStatusLine(string content)
    {
        var width = GetConsoleWidth();
        if (width <= 0)
        {
            Console.WriteLine(content);
            return;
        }

        var safe = content.Length >= width ? content[..(width - 1)] : content;
        Console.Write('\r');
        Console.Write(safe.PadRight(width - 1));
    }

    private static void WriteCompletedLine(string content)
    {
        var width = GetConsoleWidth();
        if (width > 0)
            Console.Write("\r" + new string(' ', width - 1) + "\r");

        Console.WriteLine(content);
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Console.BufferWidth;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private List<string> BuildRegistryDependencyPlan(ProjectConfig projectConfig)
    {
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var dependencyText in projectConfig.Dependencies)
        {
            var spec = ProjectDependencySpec.Parse(dependencyText);
            if (spec.IsGit)
                continue;

            CollectRegistryDependency(spec.Name, spec.VariantName, resolved, ordered);
        }

        return ordered;
    }

    private void CollectRegistryDependency(
        string packageName,
        string? variantName,
        HashSet<string> resolved,
        List<string> ordered)
    {
        var package = _registry.Find(packageName);
        if (package is null)
            return;

        if (!resolved.Add(package.Name))
            return;

        foreach (var dependency in package.Dependencies)
        {
            var dependencySpec = DependencySpec.Parse(dependency);
            CollectRegistryDependency(
                dependencySpec.PackageName,
                dependencySpec.VariantName,
                resolved,
                ordered);
        }

        if (!string.IsNullOrWhiteSpace(variantName) &&
            package.Variants.TryGetValue(variantName, out var variant))
        {
            foreach (var dependency in variant.Dependencies)
            {
                var dependencySpec = DependencySpec.Parse(dependency);
                CollectRegistryDependency(
                    dependencySpec.PackageName,
                    dependencySpec.VariantName,
                    resolved,
                    ordered);
            }
        }

        ordered.Add(package.Name);
    }

    private static string FormatDependencyList(List<string> dependencies)
    {
        if (dependencies.Count <= 6)
            return $"[{string.Join(", ", dependencies)}]";

        var preview = string.Join(", ", dependencies.Take(6));
        return $"[{preview}, +{dependencies.Count - 6} more]";
    }

    private void WriteProgressLine(string content)
    {
        if (Verbose)
            return;

        Console.WriteLine(content);
    }

    private static string? ParseConfigureProgress(string line)
    {
        if (line.Contains("Populating ", StringComparison.OrdinalIgnoreCase))
        {
            var depName = line[(line.IndexOf("Populating ", StringComparison.OrdinalIgnoreCase) + "Populating ".Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(depName))
                return $"fetch dependency {depName}";
        }

        var extracted = ExtractDependencyName(line);
        if (!string.IsNullOrWhiteSpace(extracted))
            return $"fetch dependency {extracted}";

        if (line.Contains("Configuring done", StringComparison.OrdinalIgnoreCase))
            return "finalize configure";
        if (line.Contains("Generating done", StringComparison.OrdinalIgnoreCase))
            return "generate build graph";

        return null;
    }

    private static string? ParseBuildProgress(string line)
    {
        var depName = ExtractDependencyName(line);
        if (!string.IsNullOrWhiteSpace(depName))
            return $"build dependency {depName}";

        if (line.Contains("Building CXX object", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Building C object", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Building CXX module", StringComparison.OrdinalIgnoreCase))
        {
            return "compile project sources";
        }

        if (line.Contains("Linking CXX executable", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Linking CXX static library", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Linking CXX shared library", StringComparison.OrdinalIgnoreCase))
        {
            return "link project";
        }

        if (line.Contains("no work to do", StringComparison.OrdinalIgnoreCase))
            return "no rebuild needed";

        return null;
    }

    private static string? ParseInstallProgress(string line)
    {
        if (line.Contains("Installing:", StringComparison.OrdinalIgnoreCase))
            return "install project artifacts";

        if (line.Contains("Up-to-date:", StringComparison.OrdinalIgnoreCase))
            return "artifacts already installed";

        return null;
    }

    private static string? ExtractDependencyName(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var forIndex = line.IndexOf("for '", StringComparison.OrdinalIgnoreCase);
        if (forIndex >= 0)
        {
            var start = forIndex + "for '".Length;
            var end = line.IndexOf('\'', start);
            if (end > start)
            {
                var quotedToken = line[start..end];
                var normalized = NormalizeDependencyName(quotedToken);
                if (!string.IsNullOrWhiteSpace(normalized))
                    return normalized;
            }
        }

        var depsIndex = line.IndexOf("_deps/", StringComparison.OrdinalIgnoreCase);
        if (depsIndex < 0)
            depsIndex = line.IndexOf("_deps\\", StringComparison.OrdinalIgnoreCase);

        if (depsIndex < 0)
            return null;

        var depStart = depsIndex + "_deps/".Length;
        var depEnd = depStart;
        while (depEnd < line.Length)
        {
            var ch = line[depEnd];
            if (ch is '/' or '\\' or ' ' or ')' or '(' or ':')
                break;
            depEnd++;
        }

        if (depEnd <= depStart)
            return null;

        var token = line[depStart..depEnd];
        return NormalizeDependencyName(token);
    }

    private static string? NormalizeDependencyName(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim();
        string[] suffixes =
        [
            "-populate-prefix",
            "-populate",
            "-subbuild",
            "-build",
            "-src",
            "-stamp",
        ];

        foreach (var suffix in suffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed class CommandOutputBuffer
    {
        private const int MaxStoredLines = 600;
        private const int MaxDiagnosticLines = 60;
        private readonly Queue<string> _lines = new(MaxStoredLines);
        private readonly Lock _gate = new();

        public void Add(string line)
        {
            var normalized = line.TrimEnd();
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            lock (_gate)
            {
                if (_lines.Count >= MaxStoredLines)
                    _lines.Dequeue();

                _lines.Enqueue(normalized);
            }
        }

        public bool HasCompilationError()
        {
            lock (_gate)
                return _lines.Any(IsCompilationErrorLine);
        }

        public List<string> GetDiagnostics()
        {
            lock (_gate)
            {
                var snapshot = _lines.ToList();
                var selected = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                for (var i = 0; i < snapshot.Count; i++)
                {
                    var line = snapshot[i];
                    if (IsFailedLine(line))
                    {
                        AddUnique(selected, seen, line);
                        AddFailureContext(snapshot, i, selected, seen);
                    }

                    if (IsDiagnosticLine(line))
                        AddUnique(selected, seen, line);
                }

                if (selected.Count == 0)
                {
                    foreach (var line in snapshot.TakeLast(12))
                        AddUnique(selected, seen, line);
                }

                if (selected.Count <= MaxDiagnosticLines)
                    return selected;

                return selected.TakeLast(MaxDiagnosticLines).ToList();
            }
        }

        private static void AddFailureContext(
            List<string> snapshot,
            int failedLineIndex,
            List<string> output,
            HashSet<string> seen)
        {
            var contextEnd = Math.Min(snapshot.Count - 1, failedLineIndex + 3);
            for (var index = failedLineIndex + 1; index <= contextEnd; index++)
                AddUnique(output, seen, snapshot[index]);
        }

        private static void AddUnique(List<string> output, HashSet<string> seen, string line)
        {
            if (seen.Add(line))
                output.Add(line);
        }

        private static bool IsFailedLine(string line) =>
            line.StartsWith("FAILED:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" FAILED:", StringComparison.OrdinalIgnoreCase);

        private static bool IsCompilationErrorLine(string line) =>
            line.Contains(": error:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" error C", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fatal error", StringComparison.OrdinalIgnoreCase) ||
            IsFailedLine(line);

        private static bool IsDiagnosticLine(string line) =>
            IsCompilationErrorLine(line) ||
            line.Contains(": warning:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" warning C", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("undefined reference", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ld: error:", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ActivityState
    {
        private readonly Lock _gate = new();
        private string? _current;

        public bool TrySet(string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return false;

            lock (_gate)
            {
                if (string.Equals(_current, detail, StringComparison.Ordinal))
                    return false;

                _current = detail;
                return true;
            }
        }

        public string? Get()
        {
            lock (_gate)
                return _current;
        }
    }
}
