using System.Diagnostics;
using System.Text.Json;

namespace Abel.Tests;

public sealed class AbelCliIntegrationTests
{
    private static readonly string RepositoryRoot = ResolveRepositoryRoot();

    private static readonly IReadOnlyList<ExampleCase> ExampleCases =
    [
        new("ModuleNestedDependencies", "build", RequiresNetwork: false),
        new("ModuleNoDependencies", "build", RequiresNetwork: false),
        new("ProgramHeaderSrc", "run", RequiresNetwork: false),
        new("ProgramImGuiFlecsSdl", "build", RequiresNetwork: true),
        new("ProgramNestedDependencies", "run", RequiresNetwork: false),
        new("ProgramNoDependencies", "run", RequiresNetwork: false),
        new("ProgramThirdPartyDependency", "build", RequiresNetwork: true),
    ];

    public static TheoryData<string, string> LocalExampleCases =>
        BuildTheoryData(ExampleCases.Where(example => !example.RequiresNetwork));

    public static TheoryData<string, string> NetworkExampleCases =>
        BuildTheoryData(ExampleCases.Where(example => example.RequiresNetwork));

    [Fact]
    public void EveryTopLevelExample_IsCoveredByTestMatrix()
    {
        var listed = ExampleCases
            .Select(example => example.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var discovered = Directory.EnumerateDirectories(Path.Combine(RepositoryRoot, "Example"))
            .Where(directory => File.Exists(Path.Combine(directory, "project.json")))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(discovered, listed);
    }

    [Theory]
    [MemberData(nameof(LocalExampleCases))]
    [Trait("Category", "Examples")]
    public async Task LocalDependencyExamples_WorkThroughAbelCli(string relativePath, string command)
    {
        await AssertExampleCommandSucceedsAsync(relativePath, command);
    }

    [Theory]
    [MemberData(nameof(NetworkExampleCases))]
    [Trait("Category", "Examples")]
    [Trait("RequiresNetwork", "true")]
    public async Task ThirdPartyDependencyExamples_WorkThroughAbelCli(string relativePath, string command)
    {
        await AssertExampleCommandSucceedsAsync(relativePath, command);
    }

    [Fact]
    public async Task Help_PrintsUsage()
    {
        using var workspace = new TemporaryWorkspace();

        var result = await AbelCli.RunAsync(workspace.RootPath, "help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Abel - Opinionated C++ build runner", result.StandardOutput);
        Assert.Contains("Commands:", result.StandardOutput);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardError));
    }

    [Fact]
    public async Task InitExe_ThenRun_BuildsAndRunsFromConsoleInterface()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "demoapp");
        Assert.Equal(0, initResult.ExitCode);

        var projectDirectory = Path.Combine(workspace.RootPath, "demoapp");
        Assert.True(File.Exists(Path.Combine(projectDirectory, "project.json")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "main.cpp")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, ".gitignore")));

        AssertGitInitializationResult(projectDirectory, initResult.StandardError);

        var runResult = await AbelCli.RunAsync(workspace.RootPath, "run", "demoapp");

        Assert.Equal(0, runResult.ExitCode);
        Assert.Contains("build demoapp", runResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello from demoapp", runResult.StandardOutput);
    }

    [Fact]
    public async Task InitModule_CreatesGitIgnore_AndInitializesGitWhenAvailable()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "demomodule", "--type", "module");
        Assert.Equal(0, initResult.ExitCode);

        var projectDirectory = Path.Combine(workspace.RootPath, "demomodule");
        Assert.True(File.Exists(Path.Combine(projectDirectory, "project.json")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "src", "demomodule.cppm")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "src", "demomodule_impl.cpp")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, ".gitignore")));

        AssertGitInitializationResult(projectDirectory, initResult.StandardError);
    }

    [Fact]
    public async Task AddDependency_UpdatesProjectJson_AndIsIdempotent()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "depsapp");
        Assert.Equal(0, initResult.ExitCode);

        var projectDirectory = Path.Combine(workspace.RootPath, "depsapp");
        var addFirst = await AbelCli.RunAsync(workspace.RootPath, "add", "--project", projectDirectory, "sdl3");
        Assert.Equal(0, addFirst.ExitCode);
        Assert.Contains("+ sdl3", addFirst.StandardOutput);

        var dependencies = ReadDependencies(Path.Combine(projectDirectory, "project.json"));
        Assert.Contains("sdl3", dependencies);

        var addSecond = await AbelCli.RunAsync(workspace.RootPath, "add", "--project", projectDirectory, "sdl3");
        Assert.Equal(0, addSecond.ExitCode);
        Assert.Contains("No changes.", addSecond.StandardOutput);
        Assert.Single(ReadDependencies(Path.Combine(projectDirectory, "project.json")), "sdl3");
    }

    [Fact]
    public async Task ModuleAndBuild_CreatesLocalModuleAndBuildsHostProject()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "hostapp");
        Assert.Equal(0, initResult.ExitCode);

        var hostProjectDirectory = Path.Combine(workspace.RootPath, "hostapp");
        var moduleResult = await AbelCli.RunAsync(workspace.RootPath, "module", "gameplay", "--project", hostProjectDirectory);
        Assert.Equal(0, moduleResult.ExitCode);
        Assert.Contains("Created module 'gameplay'", moduleResult.StandardOutput);

        Assert.True(File.Exists(Path.Combine(hostProjectDirectory, "gameplay", "project.json")));
        Assert.True(File.Exists(Path.Combine(hostProjectDirectory, "gameplay", "src", "gameplay.cppm")));
        Assert.Contains("gameplay", ReadDependencies(Path.Combine(hostProjectDirectory, "project.json")));

        var buildResult = await AbelCli.RunAsync(workspace.RootPath, "build", hostProjectDirectory);
        Assert.Equal(0, buildResult.ExitCode);
        Assert.Contains("build gameplay", buildResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build hostapp", buildResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModuleCanHostNestedModules_AndBuildResolvesDependencyChain()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "rootapp");
        Assert.Equal(0, initResult.ExitCode);

        var rootProjectDirectory = Path.Combine(workspace.RootPath, "rootapp");
        var gameplayResult = await AbelCli.RunAsync(workspace.RootPath, "module", "gameplay", "--project", rootProjectDirectory);
        Assert.Equal(0, gameplayResult.ExitCode);

        var gameplayProjectDirectory = Path.Combine(rootProjectDirectory, "gameplay");
        var nestedResult = await AbelCli.RunAsync(workspace.RootPath, "module", "ai", "--project", gameplayProjectDirectory);
        Assert.Equal(0, nestedResult.ExitCode);

        Assert.Contains("gameplay", ReadDependencies(Path.Combine(rootProjectDirectory, "project.json")));
        Assert.Contains("ai", ReadDependencies(Path.Combine(gameplayProjectDirectory, "project.json")));
        Assert.True(File.Exists(Path.Combine(gameplayProjectDirectory, "ai", "project.json")));

        var buildResult = await AbelCli.RunAsync(workspace.RootPath, "build", rootProjectDirectory);
        Assert.Equal(0, buildResult.ExitCode);
        Assert.Contains("build ai", buildResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build gameplay", buildResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build rootapp", buildResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModuleWithoutProjectOption_UsesNearestParentProject()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "nearesthost");
        Assert.Equal(0, initResult.ExitCode);

        var rootProjectDirectory = Path.Combine(workspace.RootPath, "nearesthost");
        var gameplayResult = await AbelCli.RunAsync(workspace.RootPath, "module", "gameplay", "--project", rootProjectDirectory);
        Assert.Equal(0, gameplayResult.ExitCode);

        var gameplayProjectDirectory = Path.Combine(rootProjectDirectory, "gameplay");
        var nestedResult = await AbelCli.RunAsync(gameplayProjectDirectory, "module", "ai");
        Assert.Equal(0, nestedResult.ExitCode);

        Assert.Contains("gameplay", ReadDependencies(Path.Combine(rootProjectDirectory, "project.json")));
        Assert.DoesNotContain("ai", ReadDependencies(Path.Combine(rootProjectDirectory, "project.json")));
        Assert.Contains("ai", ReadDependencies(Path.Combine(gameplayProjectDirectory, "project.json")));
    }

    [Fact]
    public async Task ModuleWithPartitions_GeneratesPartitionScaffoldingAndBuilds()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "partitionhost");
        Assert.Equal(0, initResult.ExitCode);

        var hostProjectDirectory = Path.Combine(workspace.RootPath, "partitionhost");
        var moduleResult = await AbelCli.RunAsync(
            workspace.RootPath,
            "module",
            "gameplay",
            "--project",
            hostProjectDirectory,
            "--partition",
            "ecs",
            "--partition",
            "systems.pathing");

        Assert.Equal(0, moduleResult.ExitCode);

        var moduleProjectDirectory = Path.Combine(hostProjectDirectory, "gameplay");
        Assert.True(File.Exists(Path.Combine(moduleProjectDirectory, "src", "gameplay.cppm")));
        Assert.True(File.Exists(Path.Combine(moduleProjectDirectory, "src", "gameplay.ecs.cppm")));
        Assert.True(File.Exists(Path.Combine(moduleProjectDirectory, "src", "gameplay.systems_pathing.cppm")));

        var primaryInterface = File.ReadAllText(Path.Combine(moduleProjectDirectory, "src", "gameplay.cppm"));
        Assert.Contains("export import :ecs;", primaryInterface);
        Assert.Contains("export import :systems.pathing;", primaryInterface);

        var moduleSources = ReadSourcesBucket(Path.Combine(moduleProjectDirectory, "project.json"), "modules");
        Assert.Contains("src/gameplay.cppm", moduleSources);
        Assert.Contains("src/gameplay.ecs.cppm", moduleSources);
        Assert.Contains("src/gameplay.systems_pathing.cppm", moduleSources);

        var buildResult = await AbelCli.RunAsync(workspace.RootPath, "build", hostProjectDirectory);
        Assert.Equal(0, buildResult.ExitCode);
    }

    [Fact]
    public async Task BuildWithoutPath_UsesCurrentProjectDirectory()
    {
        using var workspace = new TemporaryWorkspace();

        var initResult = await AbelCli.RunAsync(workspace.RootPath, "init", "cwdapp");
        Assert.Equal(0, initResult.ExitCode);

        var projectDirectory = Path.Combine(workspace.RootPath, "cwdapp");
        var buildResult = await AbelCli.RunAsync(projectDirectory, "build");

        Assert.Equal(0, buildResult.ExitCode);
        Assert.Contains("build cwdapp", buildResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertExampleCommandSucceedsAsync(string relativePath, string command)
    {
        using var workspace = new TemporaryWorkspace();

        var sourceDirectory = Path.Combine(RepositoryRoot, "Example", relativePath);
        Assert.True(Directory.Exists(sourceDirectory), $"Example directory not found: '{sourceDirectory}'.");

        var projectDirectory = Path.Combine(workspace.RootPath, "example-under-test");
        CopyDirectoryRecursive(sourceDirectory, projectDirectory);

        var projectJsonPath = Path.Combine(projectDirectory, "project.json");
        Assert.True(File.Exists(projectJsonPath), $"project.json not found for example '{relativePath}'.");

        var projectName = ReadProjectName(projectJsonPath);
        var result = await AbelCli.RunAsync(
            workspace.RootPath,
            TimeSpan.FromMinutes(10),
            command,
            projectDirectory).ConfigureAwait(false);

        Assert.True(
            result.ExitCode == 0,
            $"Example '{relativePath}' failed with command '{command}'. Exit code: {result.ExitCode}\n" +
            $"stdout:\n{result.StandardOutput}\n" +
            $"stderr:\n{result.StandardError}");

        Assert.Contains($"build {projectName}", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        if (command.Equals("run", StringComparison.OrdinalIgnoreCase))
            Assert.Contains($"run {projectName}", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    private static TheoryData<string, string> BuildTheoryData(IEnumerable<ExampleCase> examples)
    {
        var data = new TheoryData<string, string>();
        foreach (var example in examples)
            data.Add(example.RelativePath, example.Command);
        return data;
    }

    private static IReadOnlyList<string> ReadDependencies(string projectJsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(projectJsonPath));
        if (!document.RootElement.TryGetProperty("dependencies", out var dependenciesElement) ||
            dependenciesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var dependencies = new List<string>();
        foreach (var dependency in dependenciesElement.EnumerateArray())
        {
            if (dependency.ValueKind == JsonValueKind.String && dependency.GetString() is { } value)
                dependencies.Add(value);
        }

        return dependencies;
    }

    private static IReadOnlyList<string> ReadSourcesBucket(string projectJsonPath, string bucketName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(projectJsonPath));
        if (!document.RootElement.TryGetProperty("sources", out var sourcesElement) ||
            sourcesElement.ValueKind != JsonValueKind.Object ||
            !sourcesElement.TryGetProperty(bucketName, out var bucketElement) ||
            bucketElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var value in bucketElement.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } item)
                values.Add(item);
        }

        return values;
    }

    private static string ReadProjectName(string projectJsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(projectJsonPath));
        if (!document.RootElement.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            throw new InvalidOperationException($"Missing or invalid 'name' in '{projectJsonPath}'.");
        }

        return nameElement.GetString()!;
    }

    private static void AssertGitInitializationResult(string projectDirectory, string standardError)
    {
        if (IsGitAvailable(projectDirectory))
        {
            Assert.True(
                Directory.Exists(Path.Combine(projectDirectory, ".git")),
                "Expected git repository initialization to create a .git directory.");
            return;
        }

        Assert.Contains("warn:", standardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git init", standardError, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGitAvailable(string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };

            startInfo.ArgumentList.Add("--version");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
    {
        var source = new DirectoryInfo(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in source.GetFiles())
        {
            var destination = Path.Combine(destinationDirectory, file.Name);
            file.CopyTo(destination, overwrite: true);
        }

        foreach (var directory in source.GetDirectories())
        {
            var destination = Path.Combine(destinationDirectory, directory.Name);
            CopyDirectoryRecursive(directory.FullName, destination);
        }
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var hasSolution = File.Exists(Path.Combine(current.FullName, "Abel.slnx"));
            var hasExampleFolder = Directory.Exists(Path.Combine(current.FullName, "Example"));
            if (hasSolution && hasExampleFolder)
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root from base directory '{AppContext.BaseDirectory}'.");
    }

    private sealed record ExampleCase(string RelativePath, string Command, bool RequiresNetwork);

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "abel-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; failed deletion should not hide test results.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; failed deletion should not hide test results.
            }
        }
    }

    private static class AbelCli
    {
        private static readonly string ExecutablePath = ResolveExecutablePath();
        private static readonly bool UseDotnetHost = Path.GetExtension(ExecutablePath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        private static readonly string DotnetExecutable = "dotnet";

        public static Task<CliResult> RunAsync(string workingDirectory, params string[] args) =>
            RunAsync(workingDirectory, TimeSpan.FromMinutes(2), args);

        public static async Task<CliResult> RunAsync(string workingDirectory, TimeSpan timeout, params string[] args)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = UseDotnetHost ? DotnetExecutable : ExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };

            if (UseDotnetHost)
                startInfo.ArgumentList.Add(ExecutablePath);

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();

            var completed = await Task.WhenAny(waitTask, Task.Delay(timeout));
            if (completed != waitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process has already exited.
                }

                throw new TimeoutException(
                    $"Abel CLI timed out after {timeout}. Args: {string.Join(' ', args)}. Working directory: {workingDirectory}");
            }

            var standardOutput = await outputTask;
            var standardError = await errorTask;
            return new CliResult(process.ExitCode, standardOutput, standardError);
        }

        private static string ResolveExecutablePath()
        {
            var baseDirectory = AppContext.BaseDirectory;

            var nativeHostName = OperatingSystem.IsWindows() ? "Abel.exe" : "Abel";
            var nativeHostPath = Path.Combine(baseDirectory, nativeHostName);
            if (File.Exists(nativeHostPath))
                return nativeHostPath;

            var dllPath = Path.Combine(baseDirectory, "Abel.dll");
            if (File.Exists(dllPath))
                return dllPath;

            throw new FileNotFoundException(
                $"Could not locate Abel executable in '{baseDirectory}'. Expected '{nativeHostName}' or 'Abel.dll'.");
        }
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
