using Abel.Core;
using System.Text.Json;

namespace Abel;

internal static class ModuleScaffolder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool TryCreateModule(IReadOnlyList<string> args, bool verbose)
    {
        try
        {
            var options = ParseOptions(args);
            var hostProjectFilePath = ResolveProjectFilePath(options);
            var hostConfig = ReadProjectConfig(hostProjectFilePath);
            ValidateHostProject(hostConfig, hostProjectFilePath);

            var context = BuildContext(options, hostProjectFilePath, hostConfig);
            var createdFiles = CreateModuleFiles(context);

            WriteProjectConfig(hostProjectFilePath, hostConfig);
            PrintResult(context, createdFiles, verbose);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return false;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return false;
        }
    }

    private static CreateModuleOptions ParseOptions(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        string? projectPath = null;
        var projectPathExplicitlySet = false;
        string? moduleDirectory = null;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (TryHandleOption(
                    args,
                    ref i,
                    token,
                    ref projectPath,
                    ref projectPathExplicitlySet,
                    ref moduleDirectory))
                continue;

            positional.Add(token);
        }

        if (positional.Count == 0)
            throw new InvalidOperationException("Command 'module' requires a module name. Example: abel module gameplay");
        if (positional.Count > 1)
            throw new InvalidOperationException($"Unexpected extra argument '{positional[1]}' for command 'module'.");

        return new CreateModuleOptions(positional[0], projectPath, projectPathExplicitlySet, moduleDirectory);
    }

    private static bool TryHandleOption(
        IReadOnlyList<string> args,
        ref int index,
        string token,
        ref string? projectPath,
        ref bool projectPathExplicitlySet,
        ref string? moduleDirectory)
    {
        if (token.Equals("--project", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("-p", StringComparison.OrdinalIgnoreCase))
        {
            projectPath = ReadOptionValue(args, ref index, token);
            projectPathExplicitlySet = true;
            return true;
        }

        if (token.Equals("--dir", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("-d", StringComparison.OrdinalIgnoreCase))
        {
            moduleDirectory = ReadOptionValue(args, ref index, token);
            return true;
        }

        if (token.StartsWith('-'))
            throw new InvalidOperationException($"Unknown option '{token}' for command 'module'.");

        return false;
    }

    private static string ReadOptionValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        var valueIndex = index + 1;
        if (valueIndex >= args.Count)
            throw new InvalidOperationException($"Option '{optionName}' requires a value.");

        var value = args[valueIndex];
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            throw new InvalidOperationException($"Option '{optionName}' requires a non-empty value.");

        index = valueIndex;
        return value;
    }

    private static string ResolveProjectFilePath(CreateModuleOptions options)
    {
        if (options.ProjectPathExplicitlySet)
            return ResolveProjectFilePathFromInput(options.ProjectPath ?? Environment.CurrentDirectory);

        return DiscoverProjectFilePath(Environment.CurrentDirectory);
    }

    private static string ResolveProjectFilePathFromInput(string inputPath)
    {
        var fullPath = Path.GetFullPath(inputPath);
        if (File.Exists(fullPath))
        {
            var fileName = Path.GetFileName(fullPath);
            if (!fileName.Equals("project.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"File '{inputPath}' is not project.json.");
            return fullPath;
        }

        if (!Directory.Exists(fullPath))
            throw new InvalidOperationException($"Path '{inputPath}' does not exist.");

        var projectFilePath = Path.Combine(fullPath, "project.json");
        if (!File.Exists(projectFilePath))
            throw new InvalidOperationException($"Directory '{inputPath}' does not contain a project.json.");

        return projectFilePath;
    }

    private static string DiscoverProjectFilePath(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        string? firstProjectFilePath = null;

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "project.json");
            if (!File.Exists(candidate))
            {
                current = current.Parent;
                continue;
            }

            firstProjectFilePath ??= candidate;
            if (IsExecutableProject(candidate))
                return candidate;

            current = current.Parent;
        }

        if (firstProjectFilePath is not null)
            return firstProjectFilePath;

        throw new InvalidOperationException(
            "Could not find a project.json in the current directory or any parent directory. " +
            "Use '--project <path>' to specify one explicitly.");
    }

    private static bool IsExecutableProject(string projectFilePath)
    {
        var config = ReadProjectConfig(projectFilePath);
        return config.ProjectOutputType == OutputType.exe;
    }

    private static ProjectConfig ReadProjectConfig(string projectFilePath)
    {
        var json = File.ReadAllText(projectFilePath);
        var config = JsonSerializer.Deserialize<ProjectConfig>(json);
        return config ?? throw new InvalidOperationException($"Could not parse '{projectFilePath}'.");
    }

    private static void ValidateHostProject(ProjectConfig hostConfig, string hostProjectFilePath)
    {
        if (hostConfig.ProjectOutputType == OutputType.exe)
            return;

        throw new InvalidOperationException(
            $"Project '{hostConfig.Name}' in '{hostProjectFilePath}' is not an executable program.");
    }

    private static ModuleContext BuildContext(
        CreateModuleOptions options,
        string hostProjectFilePath,
        ProjectConfig hostConfig)
    {
        var moduleProjectName = options.ModuleName.Trim();
        EnsureModuleNameIsValid(moduleProjectName);
        EnsureNotHostProjectName(moduleProjectName, hostConfig.Name);

        var hostProjectDirectory = Path.GetDirectoryName(hostProjectFilePath)
                                   ?? throw new InvalidOperationException($"Invalid project path '{hostProjectFilePath}'.");

        var moduleDirectoryOption = string.IsNullOrWhiteSpace(options.ModuleDirectory)
            ? moduleProjectName
            : options.ModuleDirectory;

        var moduleDirectoryPath = ResolveModuleDirectoryPath(hostProjectDirectory, moduleDirectoryOption);
        EnsureModuleDirectoryCanBeCreated(moduleDirectoryPath);

        var moduleIdentifier = ToModuleIdentifier(moduleProjectName);
        var dependencyAdded = TryAddDependency(hostConfig, moduleProjectName);

        return new ModuleContext(
            hostConfig.Name,
            hostProjectDirectory,
            moduleProjectName,
            moduleDirectoryPath,
            moduleIdentifier,
            hostConfig.CXXStandard,
            dependencyAdded);
    }

    private static string ResolveModuleDirectoryPath(string hostProjectDirectory, string moduleDirectoryOption)
    {
        if (Path.IsPathRooted(moduleDirectoryOption))
            throw new InvalidOperationException("Option '--dir' must be a path relative to the host project.");

        var moduleDirectoryPath = Path.GetFullPath(Path.Combine(hostProjectDirectory, moduleDirectoryOption));
        if (!IsSubPath(hostProjectDirectory, moduleDirectoryPath))
            throw new InvalidOperationException("Option '--dir' must stay within the host project directory.");

        return moduleDirectoryPath;
    }

    private static bool IsSubPath(string rootPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        if (normalizedCandidate.Equals(normalizedRoot, comparison))
            return true;

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootPrefix, comparison);
    }

    private static void EnsureModuleDirectoryCanBeCreated(string moduleDirectoryPath)
    {
        if (!Directory.Exists(moduleDirectoryPath))
            return;

        if (Directory.EnumerateFileSystemEntries(moduleDirectoryPath).Any())
            throw new InvalidOperationException($"Directory '{moduleDirectoryPath}' already exists and is not empty.");
    }

    private static void EnsureModuleNameIsValid(string moduleProjectName)
    {
        if (string.IsNullOrWhiteSpace(moduleProjectName))
            throw new InvalidOperationException("Module name cannot be empty.");

        foreach (var ch in moduleProjectName)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
                continue;

            throw new InvalidOperationException(
                $"Module name '{moduleProjectName}' contains unsupported character '{ch}'. Use letters, digits, '_' or '-'.");
        }
    }

    private static void EnsureNotHostProjectName(string moduleProjectName, string hostProjectName)
    {
        if (!moduleProjectName.Equals(hostProjectName, StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException("Module name must be different from the host project name.");
    }

    private static bool TryAddDependency(ProjectConfig hostConfig, string moduleProjectName)
    {
        var existing = new HashSet<string>(hostConfig.Dependencies, StringComparer.OrdinalIgnoreCase);
        if (!existing.Add(moduleProjectName))
            return false;

        hostConfig.Dependencies.Add(moduleProjectName);
        return true;
    }

    private static List<string> CreateModuleFiles(ModuleContext context)
    {
        Directory.CreateDirectory(context.ModuleDirectoryPath);
        Directory.CreateDirectory(Path.Combine(context.ModuleDirectoryPath, "src"));

        var moduleFile = $"src/{context.ModuleIdentifier}.cppm";
        var implFile = $"src/{context.ModuleIdentifier}_impl.cpp";

        var files = new List<(string RelativePath, string Content)>
        {
            ("project.json", BuildModuleProjectJson(context, moduleFile, implFile)),
            (moduleFile, BuildModuleInterfaceSource(context.ModuleIdentifier)),
            (implFile, BuildModuleImplementationSource(context.ModuleIdentifier)),
        };

        var created = new List<string>(files.Count);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(context.ModuleDirectoryPath, file.RelativePath), file.Content);
            created.Add(file.RelativePath);
        }

        return created;
    }

    private static string BuildModuleProjectJson(ModuleContext context, string moduleFile, string implFile)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = context.ModuleProjectName,
            output_type = "library",
            cxx_standard = context.CXXStandard,
            sources = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["modules"] = [moduleFile],
                ["private"] = [implFile],
            },
            dependencies = Array.Empty<string>(),
            tests = new
            {
                files = Array.Empty<string>(),
            },
        }, JsonOptions);

        return json + "\n";
    }

    private static string BuildModuleInterfaceSource(string moduleIdentifier) =>
        $"export module {moduleIdentifier};\n\n" +
        "export int add(int a, int b);\n";

    private static string BuildModuleImplementationSource(string moduleIdentifier) =>
        $"module {moduleIdentifier};\n\n" +
        "int add(int a, int b)\n" +
        "{\n" +
        "    return a + b;\n" +
        "}\n";

    private static void WriteProjectConfig(string projectFilePath, ProjectConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(projectFilePath, json + "\n");
    }

    private static void PrintResult(ModuleContext context, List<string> createdFiles, bool verbose)
    {
        Console.WriteLine($"Created module '{context.ModuleProjectName}' at '{context.ModuleDirectoryPath}'.");
        if (context.DependencyAdded)
            Console.WriteLine($"Added dependency '{context.ModuleProjectName}' to '{context.HostProjectName}'.");
        else
            Console.WriteLine($"Dependency '{context.ModuleProjectName}' already exists in '{context.HostProjectName}'.");

        if (verbose)
        {
            foreach (var file in createdFiles)
                Console.WriteLine($"  + {Path.Combine(context.ModuleDirectoryPath, file)}");
        }

        Console.WriteLine("Next step:");
        Console.WriteLine($"  abel build {context.HostProjectDirectory}");
    }

    private static string ToModuleIdentifier(string projectName)
    {
        Span<char> buffer = stackalloc char[projectName.Length];
        var used = 0;

        foreach (var ch in projectName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[used++] = char.ToLowerInvariant(ch);
                continue;
            }

            buffer[used++] = '_';
        }

        var raw = used == 0 ? "" : new string(buffer[..used]);
        if (string.IsNullOrWhiteSpace(raw))
            raw = "module_name";
        if (char.IsDigit(raw[0]))
            raw = $"m_{raw}";

        var normalized = raw.Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "module_name" : normalized;
    }

    private sealed record CreateModuleOptions(
        string ModuleName,
        string? ProjectPath,
        bool ProjectPathExplicitlySet,
        string? ModuleDirectory);
    private sealed record ModuleContext(
        string HostProjectName,
        string HostProjectDirectory,
        string ModuleProjectName,
        string ModuleDirectoryPath,
        string ModuleIdentifier,
        int CXXStandard,
        bool DependencyAdded);
}
