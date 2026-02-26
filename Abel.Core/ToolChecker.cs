using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

namespace Abel.Core;

/// <summary>
/// Checks that required build tools (C++ compiler, CMake, Ninja) are installed
/// and reachable on PATH. Prints warnings for anything missing but never aborts —
/// the build might still work if tools are in non-standard locations.
/// </summary>
public static class ToolChecker
{
    public record ToolResult(string Name, bool Found, string? Version, string? Path);
    private sealed record ToolDescriptor(string Name, string Purpose, bool Required, string? MissingHint = null);

    /// <summary>
    /// Runs all checks and prints a summary. Returns true if everything looks good.
    /// </summary>
    public static async Task<bool> CheckAll(bool verbose = false)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  [check] Verifying Abel build prerequisites...");
        Console.ResetColor();
        Console.WriteLine("  [why]   Abel needs CMake (configure), Ninja (build), and at least one C++ compiler.");

        var descriptors = BuildToolDescriptors();

        var results = new List<ToolResult>
        {
            // CMake — required on all platforms
            await Probe("cmake", "--version", ParseCmakeVersion),
            // Ninja — required on all platforms
            await Probe("ninja", "--version", ParseNinjaVersion),
            // ClangFormat - optional but recommended for formatting
            await Probe("clang-format", "--version", ParseClangFormatVersion),
        };

        // C++ compiler — OS-specific
        results.AddRange(await ProbeCompilers());

        // Print results
        bool allGood = true;
        bool hasCompiler = false;

        foreach (var descriptor in descriptors)
        {
            var result = results.FirstOrDefault(item =>
                item.Name.Equals(descriptor.Name, StringComparison.OrdinalIgnoreCase));

            if (result is null)
                continue;

            if (result.Found)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [ok]    {result.Name} - {descriptor.Purpose}");
                Console.ResetColor();

                if (!string.IsNullOrWhiteSpace(result.Version))
                    Console.WriteLine($"         version: {result.Version}");

                if (verbose && !string.IsNullOrWhiteSpace(result.Path))
                    Console.WriteLine($"         path:    {result.Path}");

                // Track if at least one compiler was found
                if (IsCompiler(result.Name))
                    hasCompiler = true;
            }
            else
            {
                if (IsCompiler(result.Name))
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  [miss]  {result.Name} - {descriptor.Purpose}");
                    Console.ResetColor();
                    continue; // We'll handle missing compilers below as a group
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [warn]  {result.Name} missing - {descriptor.Purpose}");
                Console.ResetColor();

                if (!string.IsNullOrWhiteSpace(descriptor.MissingHint))
                    Console.WriteLine($"         fix: {descriptor.MissingHint}");

                allGood = false;
            }
        }

        // Check that at least one compiler is available
        if (!hasCompiler)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            if (OperatingSystem.IsWindows())
                Console.WriteLine("  [warn]  No C++ compiler found. Install clang++ (LLVM) or Visual Studio C++ tools (cl.exe).");
            else
                Console.WriteLine("  [warn]  No C++ compiler found. Install g++ or clang++.");
            Console.ResetColor();
            Console.WriteLine("         reason: Abel cannot compile C++ targets without a compiler toolchain.");
            allGood = false;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  [ok]    compiler toolchain - at least one C++ compiler is available.");
            Console.ResetColor();
        }

        if (allGood)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  [check] Environment is ready.");
            Console.ResetColor();
        }
        else if (!verbose)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [check] Some prerequisites are missing. Run 'abel check --verbose' for paths/details.");
            Console.ResetColor();
        }

        return allGood;
    }

    private static List<ToolDescriptor> BuildToolDescriptors()
    {
        var descriptors = new List<ToolDescriptor>
        {
            new(
                Name: "cmake",
                Purpose: "generates the build graph from CMakeLists.txt",
                Required: true,
                MissingHint: "Install CMake and make sure 'cmake' is on PATH."),
            new(
                Name: "ninja",
                Purpose: "build executor used by Abel's generated build files",
                Required: true,
                MissingHint: "Install Ninja and make sure 'ninja' is on PATH."),
            new(
                Name: "clang-format",
                Purpose: "formats C++ source files",
                Required: false,
                MissingHint: "Install clang-format to use the 'abel format' command."),
        };

        if (OperatingSystem.IsWindows())
        {
            descriptors.Add(new(
                Name: "clang++",
                Purpose: "C++ compiler option (LLVM)",
                Required: false));
            descriptors.Add(new(
                Name: "cl.exe (MSVC)",
                Purpose: "C++ compiler option (Visual Studio toolchain)",
                Required: false));
        }
        else
        {
            descriptors.Add(new(
                Name: "g++",
                Purpose: "C++ compiler option (GNU)",
                Required: false));
            descriptors.Add(new(
                Name: "clang++",
                Purpose: "C++ compiler option (LLVM/Clang)",
                Required: false));
        }

        return descriptors;
    }

    /// <summary>
    /// Probes the OS-appropriate set of C++ compilers.
    /// On Windows: clang++, cl (MSVC). On Linux/macOS: g++, clang++.
    /// Only one needs to be present — the user picks via CMake toolchain or defaults.
    /// </summary>
    private static async Task<List<ToolResult>> ProbeCompilers()
    {
        var results = new List<ToolResult>();

        if (OperatingSystem.IsWindows())
        {
            results.Add(await Probe("clang++", "--version", ParseClangVersion));
            results.Add(await ProbeMsvc());
        }
        else
        {
            results.Add(await Probe("g++", "--version", ParseGccVersion));
            results.Add(await Probe("clang++", "--version", ParseClangVersion));
        }

        return results;
    }

    /// <summary>
    /// Tries to run an executable with the given arguments and extract a version string.
    /// Returns Found=false if the process fails to start or returns non-zero.
    /// </summary>
    private static async Task<ToolResult> Probe(string executable, string arguments, Func<string, string?> parseVersion)
    {
        try
        {
            var result = await Cli.Wrap(executable)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
                return new ToolResult(executable, false, null, null);

            var output = result.StandardOutput + result.StandardError;
            var version = parseVersion(output);
            var path = await WhichAsync(executable);

            return new ToolResult(executable, true, version, path);
        }
        catch
        {
            // Process failed to start — not on PATH
            return new ToolResult(executable, false, null, null);
        }
    }

    /// <summary>
    /// MSVC's cl.exe is special — it's not typically on PATH. It lives inside the
    /// Visual Studio installation and requires vcvarsall.bat to set up the environment.
    /// We check if vswhere.exe can find a VS install with the C++ workload.
    /// </summary>
    private static async Task<ToolResult> ProbeMsvc()
    {
        // First try cl.exe directly (works if run from Developer Command Prompt)
        try
        {
            var result = await Cli.Wrap("cl.exe")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            // cl.exe prints version info to stderr
            var version = ParseMsvcVersion(result.StandardError);
            if (version is not null)
                return new ToolResult("cl.exe (MSVC)", true, version, "cl.exe");
        }
        catch { /* not on PATH, try vswhere */ }

        // Fall back to vswhere to detect Visual Studio installation
        var vswherePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe"
        );

        if (!File.Exists(vswherePath))
            return new ToolResult("cl.exe (MSVC)", false, null, null);

        try
        {
            var result = await Cli.Wrap(vswherePath)
                .WithArguments("-latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var version = result.StandardOutput.Trim();
            if (!string.IsNullOrEmpty(version))
                return new ToolResult("cl.exe (MSVC)", true, $"VS {version}", "via vswhere");
        }
        catch { /* vswhere failed */ }

        return new ToolResult("cl.exe (MSVC)", false, null, null);
    }

    /// <summary>
    /// Resolves the full path of an executable using 'where' (Windows) or 'which' (Unix).
    /// </summary>
    private static async Task<string?> WhichAsync(string executable)
    {
        try
        {
            var cmd = OperatingSystem.IsWindows() ? "where" : "which";
            var result = await Cli.Wrap(cmd)
                .WithArguments(executable)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var path = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCompiler(string name) =>
        name is "g++" or "clang++" or "cl.exe (MSVC)";

    // ─── Version parsers ─────────────────────────────────────────────

    // "cmake version 3.28.1"
    private static string? ParseCmakeVersion(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("cmake version", StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        }
        return null;
    }

    // "1.11.1" (ninja just prints the version number)
    private static string? ParseNinjaVersion(string output) =>
        output.Trim().Split('\n').FirstOrDefault()?.Trim();

    // "clang-format version 18.1.3 (https://...)"
    private static string? ParseClangFormatVersion(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("clang-format version", StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        }
        return null;
    }

    // "clang version 18.1.3 (https://...)"
    private static string? ParseClangVersion(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("clang version", StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        }
        return null;
    }

    // "g++ (Ubuntu 13.2.0-23ubuntu4) 13.2.0"
    private static string? ParseGccVersion(string output) =>
        output.Split('\n').FirstOrDefault()?.Trim();

    // "Microsoft (R) C/C++ Optimizing Compiler Version 19.38.33130 for x64"
    private static string? ParseMsvcVersion(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("C/C++", StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        }
        return null;
    }
}
