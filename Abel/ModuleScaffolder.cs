using Abel.Core;
using System.Text;
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
        var partitionNames = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (TryHandleOption(
                    args,
                    ref i,
                    token,
                    ref projectPath,
                    ref projectPathExplicitlySet,
                    ref moduleDirectory,
                    partitionNames))
                continue;

            positional.Add(token);
        }

        if (positional.Count == 0)
            throw new InvalidOperationException("Command 'module' requires a module name. Example: abel module gameplay");
        if (positional.Count > 1)
            throw new InvalidOperationException($"Unexpected extra argument '{positional[1]}' for command 'module'.");

        return new CreateModuleOptions(
            positional[0],
            projectPath,
            projectPathExplicitlySet,
            moduleDirectory,
            ParseAndValidatePartitionNames(partitionNames));
    }

    private static bool TryHandleOption(
        IReadOnlyList<string> args,
        ref int index,
        string token,
        ref string? projectPath,
        ref bool projectPathExplicitlySet,
        ref string? moduleDirectory,
        List<string> partitionNames)
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

        if (token.Equals("--partition", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("--part", StringComparison.OrdinalIgnoreCase))
        {
            partitionNames.Add(ReadOptionValue(args, ref index, token));
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

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "project.json");
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not find a project.json in the current directory or any parent directory. " +
            "Use '--project <path>' to specify one explicitly.");
    }

    private static ProjectConfig ReadProjectConfig(string projectFilePath)
    {
        var json = File.ReadAllText(projectFilePath);
        var config = JsonSerializer.Deserialize<ProjectConfig>(json);
        return config ?? throw new InvalidOperationException($"Could not parse '{projectFilePath}'.");
    }

    private static void ValidateHostProject(ProjectConfig hostConfig, string hostProjectFilePath)
    {
        if (hostConfig.ProjectOutputType is OutputType.exe or OutputType.library)
            return;

        throw new InvalidOperationException(
            $"Project '{hostConfig.Name}' in '{hostProjectFilePath}' is not a supported host project type.");
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
        var partitions = BuildPartitionDefinitions(options.PartitionNames);
        var dependencyAdded = TryAddDependency(hostConfig, moduleProjectName);

        return new ModuleContext(
            hostConfig.Name,
            hostProjectDirectory,
            moduleProjectName,
            moduleDirectoryPath,
            moduleIdentifier,
            partitions,
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
        var moduleSources = new List<string> { moduleFile };
        var privateSources = new List<string>();
        var files = new List<(string RelativePath, string Content)>();

        if (context.Partitions.Count == 0)
        {
            var implFile = $"src/{context.ModuleIdentifier}_impl.cpp";
            privateSources.Add(implFile);

            files.Add((moduleFile, BuildModuleInterfaceSource(context.ModuleIdentifier)));
            files.Add((implFile, BuildModuleImplementationSource(context.ModuleIdentifier)));
        }
        else
        {
            files.Add((moduleFile, BuildModulePrimaryInterfaceSource(context.ModuleIdentifier, context.Partitions)));

            foreach (var partition in context.Partitions)
            {
                var partitionInterfaceFile = $"src/{context.ModuleIdentifier}.{partition.FileNameToken}.cppm";

                moduleSources.Add(partitionInterfaceFile);

                files.Add((partitionInterfaceFile, BuildPartitionInterfaceSource(
                    context.ModuleIdentifier,
                    partition.PartitionName,
                    partition.ExportedFunctionName)));
            }
        }

        files.Insert(0, ("project.json", BuildModuleProjectJson(context, moduleSources, privateSources)));

        var created = new List<string>(files.Count);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(context.ModuleDirectoryPath, file.RelativePath), file.Content);
            created.Add(file.RelativePath);
        }

        return created;
    }

    private static string BuildModuleProjectJson(
        ModuleContext context,
        IReadOnlyList<string> moduleSources,
        IReadOnlyList<string> privateSources)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = context.ModuleProjectName,
            output_type = "library",
            cxx_standard = context.CXXStandard,
            sources = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["modules"] = moduleSources.ToArray(),
                ["private"] = privateSources.ToArray(),
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

    private static string BuildModulePrimaryInterfaceSource(
        string moduleIdentifier,
        IReadOnlyList<ModulePartitionDefinition> partitions)
    {
        var builder = new StringBuilder();
        builder.Append("export module ").Append(moduleIdentifier).Append(";\n\n");

        foreach (var partition in partitions)
            builder.Append("export import :").Append(partition.PartitionName).Append(";\n");

        return builder.ToString();
    }

    private static string BuildModuleImplementationSource(string moduleIdentifier) =>
        $"module {moduleIdentifier};\n\n" +
        "int add(int a, int b)\n" +
        "{\n" +
        "    return a + b;\n" +
        "}\n";

    private static string BuildPartitionInterfaceSource(
        string moduleIdentifier,
        string partitionName,
        string exportedFunctionName) =>
        $"export module {moduleIdentifier}:{partitionName};\n\n" +
        $"export int {exportedFunctionName}()\n" +
        "{\n" +
        "    return 0;\n" +
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

        if (context.Partitions.Count > 0)
            Console.WriteLine($"Created partitions: {string.Join(", ", context.Partitions.Select(partition => partition.PartitionName))}");

        if (verbose)
        {
            foreach (var file in createdFiles)
                Console.WriteLine($"  + {Path.Combine(context.ModuleDirectoryPath, file)}");
        }

        Console.WriteLine("Next step:");
        Console.WriteLine($"  abel build {context.HostProjectDirectory}");
    }

    private static List<string> ParseAndValidatePartitionNames(List<string> rawPartitionNames)
    {
        if (rawPartitionNames.Count == 0)
            return [];

        var partitions = new List<string>(rawPartitionNames.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawName in rawPartitionNames)
        {
            var partitionName = rawName.Trim();
            ValidatePartitionName(partitionName);

            if (!seen.Add(partitionName))
                throw new InvalidOperationException($"Duplicate partition '{partitionName}' is not allowed.");

            partitions.Add(partitionName);
        }

        return partitions;
    }

    private static void ValidatePartitionName(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
            throw new InvalidOperationException("Option '--partition' requires a non-empty value.");

        var segments = partitionName.Split('.');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
                throw new InvalidOperationException(
                    $"Partition '{partitionName}' is invalid. Dot-separated segments cannot be empty.");

            if (!IsIdentifierStart(segment[0]))
            {
                throw new InvalidOperationException(
                    $"Partition '{partitionName}' is invalid. Segment '{segment}' must start with a letter or '_'.");
            }

            foreach (var ch in segment)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_')
                    continue;

                throw new InvalidOperationException(
                    $"Partition '{partitionName}' contains unsupported character '{ch}'. Use letters, digits, '_' and optional dots.");
            }
        }
    }

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static List<ModulePartitionDefinition> BuildPartitionDefinitions(List<string> partitionNames)
    {
        if (partitionNames.Count == 0)
            return [];

        var partitions = new List<ModulePartitionDefinition>(partitionNames.Count);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var functionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var partitionName in partitionNames)
        {
            var token = partitionName.Replace(".", "_", StringComparison.Ordinal);
            var fileToken = ToModuleIdentifier(token);
            var functionName = $"partition_{fileToken}_value";

            if (!fileNames.Add(fileToken))
            {
                throw new InvalidOperationException(
                    $"Partition '{partitionName}' collides with another partition when generating filenames.");
            }

            if (!functionNames.Add(functionName))
            {
                throw new InvalidOperationException(
                    $"Partition '{partitionName}' collides with another partition when generating symbols.");
            }

            partitions.Add(new ModulePartitionDefinition(partitionName, fileToken, functionName));
        }

        return partitions;
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
        string? ModuleDirectory,
        List<string> PartitionNames);
    private sealed record ModuleContext(
        string HostProjectName,
        string HostProjectDirectory,
        string ModuleProjectName,
        string ModuleDirectoryPath,
        string ModuleIdentifier,
        List<ModulePartitionDefinition> Partitions,
        int CXXStandard,
        bool DependencyAdded);
    private sealed record ModulePartitionDefinition(
        string PartitionName,
        string FileNameToken,
        string ExportedFunctionName);
}
