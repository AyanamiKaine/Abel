using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Abel;

internal static class ProjectInitializer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int GitInitTimeoutMs = 5000;
    private const string DefaultGitIgnore = """
# Build outputs
/build/
/build-*/
/out/
/bin/
/obj/

# Abel
/.abel/

# CMake generated files
/CMakeFiles/
/CMakeCache.txt
/cmake_install.cmake
/compile_commands.json
/CTestTestfile.cmake
/Testing/
/Makefile
/install_manifest.txt

# IDE files
/.vs/
/.vscode/
/.idea/
*.suo
*.user
*.sln.docstates

# Compiler artifacts
*.o
*.obj
*.pdb
*.ilk
*.d
*.exe

# OS files
.DS_Store
Thumbs.db
""";

    private static readonly Dictionary<string, ProjectTemplate> Templates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["exe"] = new ProjectTemplate(
                Key: "exe",
                Description: "Executable project with a main.cpp entry point.",
                BuildFiles: BuildExecutableFiles),
            ["module"] = new ProjectTemplate(
                Key: "module",
                Description: "C++ module library with src/<module>.cppm and src/<module>_impl.cpp.",
                BuildFiles: BuildModuleFiles),
        };

    private static readonly Dictionary<string, string> TemplateAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["exe"] = "exe",
            ["executable"] = "exe",
            ["app"] = "exe",
            ["module"] = "module",
            ["library"] = "module",
            ["lib"] = "module",
        };

    public static bool TryInitialize(IReadOnlyList<string> args, bool verbose)
    {
        try
        {
            var options = ParseOptions(args);
            if (options.ListTemplates)
            {
                PrintTemplates();
                return true;
            }

            var template = ResolveTemplate(options.TemplateName);
            var context = CreateContext(options.ProjectName);
            var createdFiles = CreateFromTemplate(template, context);
            var gitInitWarning = TryInitializeGitRepository(context.ProjectDirectory);

            Console.WriteLine($"Initialized {template.Key} project '{context.ProjectName}' at '{context.ProjectDirectory}'.");
            if (verbose)
            {
                foreach (var file in createdFiles)
                    Console.WriteLine($"  + {file}");

                if (gitInitWarning is null)
                    Console.WriteLine("  + git repository initialized");
            }

            if (gitInitWarning is not null)
                Console.Error.WriteLine($"warn: {gitInitWarning}");

            Console.WriteLine("Next steps:");
            Console.WriteLine($"  cd {context.ProjectName}");
            Console.WriteLine(template.Key.Equals("exe", StringComparison.OrdinalIgnoreCase) ? "  abel run" : "  abel build");
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
    }

    private static InitOptions ParseOptions(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        string? name = null;
        string? templateName = null;
        var listTemplates = false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!TryHandleOption(args, ref i, token, ref name, ref templateName, ref listTemplates))
                positional.Add(token);
        }

        return BuildOptions(positional, name, templateName, listTemplates);
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

    private static ProjectTemplate ResolveTemplate(string templateName)
    {
        if (TemplateAliases.TryGetValue(templateName, out var canonicalName) &&
            Templates.TryGetValue(canonicalName, out var aliasedTemplate))
        {
            return aliasedTemplate;
        }

        if (Templates.TryGetValue(templateName, out var template))
            return template;

        throw new InvalidOperationException(
            $"Unknown init template '{templateName}'. Use 'abel init --list-templates' to see available templates.");
    }

    private static InitContext CreateContext(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new InvalidOperationException("Project name cannot be empty.");

        var cleanName = projectName.Trim();
        var projectDirectory = Path.Combine(Environment.CurrentDirectory, cleanName);
        var moduleName = ToModuleIdentifier(cleanName);

        EnsureProjectDirectoryCanBeCreated(projectDirectory);
        return new InitContext(cleanName, projectDirectory, moduleName);
    }

    private static void EnsureProjectDirectoryCanBeCreated(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
            return;
        }

        if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
            throw new InvalidOperationException($"Directory '{directoryPath}' already exists and is not empty.");
    }

    private static List<string> CreateFromTemplate(ProjectTemplate template, InitContext context)
    {
        var files = template.BuildFiles(context);
        var createdFiles = new List<string>(files.Count);

        foreach (var file in files)
        {
            var fullPath = Path.Combine(context.ProjectDirectory, file.RelativePath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            File.WriteAllText(fullPath, file.Content);
            createdFiles.Add(file.RelativePath);
        }

        return createdFiles;
    }

    private static IReadOnlyList<TemplateFile> BuildExecutableFiles(InitContext context)
    {
        var testFile = $"tests/{context.ProjectName}_test.cpp";

        var projectJson = JsonSerializer.Serialize(new
        {
            name = context.ProjectName,
            output_type = "exe",
            cxx_standard = 23,
            dependencies = Array.Empty<string>(),
            tests = new { files = new[] { testFile } },
        }, JsonOptions);

        var mainCpp =
            "#include <print>\n\n" +
            "auto main() -> int\n" +
            "{\n" +
            $"    std::println(\"Hello from {context.ProjectName}\\n\");\n" +
            "    return 0;\n" +
            "}\n";

        var testCpp =
            "#include <doctest/doctest.h>\n\n" +
            "TEST_CASE(\"hello world\")\n" +
            "{\n" +
            "    CHECK(1 + 1 == 2);\n" +
            "}\n";

        return
        [
            new TemplateFile("project.json", projectJson + "\n"),
            new TemplateFile("main.cpp", mainCpp),
            new TemplateFile(testFile, testCpp),
            new TemplateFile(".gitignore", DefaultGitIgnore),
        ];
    }

    private static IReadOnlyList<TemplateFile> BuildModuleFiles(InitContext context)
    {
        var moduleFile = $"src/{context.ModuleName}.cppm";
        var implFile = $"src/{context.ModuleName}_impl.cpp";
        var testFile = $"tests/{context.ModuleName}_test.cpp";

        var projectJson = JsonSerializer.Serialize(new
        {
            name = context.ProjectName,
            output_type = "library",
            cxx_standard = 23,
            sources = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["modules"] = [moduleFile],
                ["private"] = [implFile],
            },
            dependencies = Array.Empty<string>(),
            tests = new
            {
                files = new[] { testFile },
            },
        }, JsonOptions);

        var moduleSource =
            $"export module {context.ModuleName};\n\n" +
            "export int add(int a, int b);\n";

        var moduleImpl =
            $"module {context.ModuleName};\n\n" +
            "int add(int a, int b)\n" +
            "{\n" +
            "    return a + b;\n" +
            "}\n";

        var testCpp =
            "#include <doctest/doctest.h>\n\n" +
            $"import {context.ModuleName};\n\n" +
            $"TEST_CASE(\"{context.ModuleName}\")\n" +
            "{\n" +
            "    CHECK(add(1, 2) == 3);\n" +
            "}\n";

        return
        [
            new TemplateFile("project.json", projectJson + "\n"),
            new TemplateFile(moduleFile, moduleSource),
            new TemplateFile(implFile, moduleImpl),
            new TemplateFile(testFile, testCpp),
            new TemplateFile(".gitignore", DefaultGitIgnore),
        ];
    }

    private static string? TryInitializeGitRepository(string projectDirectory)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "init",
                    WorkingDirectory = projectDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
                return $"Could not start 'git init' in '{projectDirectory}'.";

            if (!process.WaitForExit(GitInitTimeoutMs))
            {
                TryKillProcess(process);
                return $"Timed out while running 'git init' in '{projectDirectory}'.";
            }

            var stderr = process.StandardError.ReadToEnd().Trim();
            var stdout = process.StandardOutput.ReadToEnd().Trim();

            if (process.ExitCode == 0)
                return null;

            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (string.IsNullOrWhiteSpace(details))
                return $"Failed to run 'git init' in '{projectDirectory}' (exit code {process.ExitCode}).";

            return $"Failed to run 'git init' in '{projectDirectory}' (exit code {process.ExitCode}): {details}";
        }
        catch (Win32Exception ex)
        {
            return $"Could not run 'git init' in '{projectDirectory}': {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"Could not run 'git init' in '{projectDirectory}': {ex.Message}";
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
        }
    }

    private static void PrintTemplates()
    {
        Console.WriteLine("Available init templates:");
        foreach (var template in Templates.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {template.Key,-8} {template.Description}");
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

    private static bool TryHandleOption(
        IReadOnlyList<string> args,
        ref int index,
        string token,
        ref string? name,
        ref string? templateName,
        ref bool listTemplates)
    {
        if (token.Equals("--list-templates", StringComparison.OrdinalIgnoreCase))
        {
            listTemplates = true;
            return true;
        }

        if (token.Equals("--name", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("-n", StringComparison.OrdinalIgnoreCase))
        {
            name = ReadOptionValue(args, ref index, token);
            return true;
        }

        if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("-t", StringComparison.OrdinalIgnoreCase))
        {
            templateName = ReadOptionValue(args, ref index, token);
            return true;
        }

        if (token.StartsWith('-'))
            throw new InvalidOperationException($"Unknown option '{token}' for command 'init'.");

        return false;
    }

    private static InitOptions BuildOptions(
        List<string> positional,
        string? name,
        string? templateName,
        bool listTemplates)
    {
        if (listTemplates)
        {
            if (positional.Count > 0 || name is not null || templateName is not null)
                throw new InvalidOperationException("'--list-templates' cannot be combined with other init arguments.");
            return new InitOptions(ProjectName: "", TemplateName: "exe", ListTemplates: true);
        }

        if (name is null)
        {
            if (positional.Count == 0)
                throw new InvalidOperationException("Command 'init' requires a project name.");

            name = positional[0];
            positional.RemoveAt(0);
        }

        if (templateName is null && positional.Count > 0)
        {
            templateName = positional[0];
            positional.RemoveAt(0);
        }

        if (positional.Count > 0)
            throw new InvalidOperationException($"Unexpected extra argument '{positional[0]}' for command 'init'.");

        templateName ??= "exe";
        return new InitOptions(ProjectName: name, TemplateName: templateName, ListTemplates: false);
    }

    private sealed record InitOptions(string ProjectName, string TemplateName, bool ListTemplates);
    private sealed record InitContext(string ProjectName, string ProjectDirectory, string ModuleName);
    private sealed record ProjectTemplate(string Key, string Description, Func<InitContext, IReadOnlyList<TemplateFile>> BuildFiles);
    private sealed record TemplateFile(string RelativePath, string Content);
}
