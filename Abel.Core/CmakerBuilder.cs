namespace Abel.Core;

/// <summary>
/// Fluent builder for generating CMakeLists.txt files.
///
/// CMake has two eras: "old CMake" used global variables (include_directories, link_directories)
/// that polluted everything. "Modern CMake" (3.0+) is target-based — you create a target
/// (executable or library) and attach properties directly to it. Properties have visibility:
///
///   PRIVATE   — only this target uses it (implementation detail)
///   PUBLIC    — this target AND anyone who links to it gets it (part of your API)
///   INTERFACE — only consumers get it, not the target itself (header-only libs)
///
/// This builder only generates modern, target-based CMake. Every source file, compile flag,
/// and dependency is scoped to a specific target with explicit visibility.
/// </summary>
public class CmakeBuilder
{
    private string _cmakeMinVersion = "3.28";
    private string _projectName = "";
    private string _language = "CXX";
    private int _cxxStandard = 23;

    private OutputType _outputType = OutputType.exe;

    // Sources
    private readonly List<string> _privateSources = [];
    private readonly List<string> _moduleSources = [];
    private readonly List<string> _publicHeaders = [];

    // Dependencies
    private readonly List<FindPackageDep> _findPackages = [];
    private readonly List<FetchContentDep> _fetchContents = [];
    private readonly List<WrapperPackageDep> _wrapperPackages = [];
    private readonly List<(string Key, string Value)> _cmakeOptions = [];
    private readonly List<string> _linkLibraries = [];
    private readonly BuildCompilerOptionsSet _projectCompileOptions = new();
    private readonly Dictionary<string, BuildCompilerOptionsSet> _compileOptionsByConfiguration =
        new(StringComparer.OrdinalIgnoreCase);

    // Testing
    private bool _enableTesting = false;
    private readonly List<TestTarget> _testTargets = [];

    // Install
    private bool _enableInstall = false;
    private bool _enableLegacyHeaderSrcLayout = false;

    // ─── Fluent setters ──────────────────────────────────────────────

    /// <summary>
    /// Emits: cmake_minimum_required(VERSION x.xx)
    ///
    /// This must be the first line in any CMakeLists.txt. It tells CMake the oldest version
    /// that can build this project. CMake uses this to enable/disable "policies" — behavioral
    /// changes between versions. Setting 3.28 means we opt into all modern behaviors up to 3.28,
    /// including native C++20 module support (FILE_SET CXX_MODULES) which landed in 3.28.
    ///
    /// Default: "3.28" — the minimum for C++ module support.
    /// </summary>
    public CmakeBuilder SetMinimumVersion(string version)
    {
        _cmakeMinVersion = version;
        return this;
    }

    /// <summary>
    /// Emits: project(name LANGUAGES CXX)
    ///
    /// Declares the project name and which compilers CMake needs to find. "LANGUAGES CXX" means
    /// we only need a C++ compiler. If you also have .c files, use "C CXX". The project name
    /// becomes available as the ${PROJECT_NAME} variable and is used in IDE project files.
    /// </summary>
    public CmakeBuilder SetProject(string name, string language = "CXX")
    {
        _projectName = name;
        _language = language;
        return this;
    }

    /// <summary>
    /// Emits: set(CMAKE_CXX_STANDARD 23), CMAKE_CXX_STANDARD_REQUIRED ON, CMAKE_CXX_EXTENSIONS OFF
    ///
    /// Controls which C++ standard the compiler targets. STANDARD_REQUIRED ON means CMake will
    /// error if the compiler doesn't support this standard (instead of silently falling back).
    /// EXTENSIONS OFF disables compiler-specific extensions (like GNU's __attribute__), keeping
    /// your code portable across compilers. For libraries, we also emit target_compile_features()
    /// with PUBLIC visibility so consumers automatically inherit the minimum standard requirement.
    ///
    /// Default: 23 (C++23).
    /// </summary>
    public CmakeBuilder SetCxxStandard(int standard)
    {
        _cxxStandard = standard;
        return this;
    }

    /// <summary>
    /// Controls whether we emit add_executable() or add_library(... STATIC).
    ///
    /// Executables are standalone programs with a main(). Libraries are compiled code meant to
    /// be linked into other targets. STATIC means the .a/.lib is baked directly into the consumer
    /// at link time (as opposed to SHARED/.so/.dll which is loaded at runtime). Abel defaults to
    /// STATIC because it avoids the headache of runtime library paths, and the linker strips
    /// unused code anyway.
    /// </summary>
    public CmakeBuilder SetOutputType(OutputType type)
    {
        _outputType = type;
        return this;
    }

    /// <summary>
    /// Emits: target_sources(name PRIVATE file1.cpp file2.cpp ...)
    ///
    /// PRIVATE sources are implementation files (.cpp, .cc) that only this target compiles.
    /// Other targets that link to this library will NOT see or compile these files — they're
    /// internal. This is the correct scope for .cpp files because consumers only need your
    /// public headers or module interfaces, not your implementation.
    /// </summary>
    public CmakeBuilder AddPrivateSources(params string[] files)
    {
        _privateSources.AddRange(files);
        return this;
    }

    /// <summary>
    /// Emits: target_sources(name PUBLIC FILE_SET CXX_MODULES FILES math.cppm ...)
    ///
    /// C++20 modules replace the old #include model. A .cppm file declares "export module X;"
    /// and consumers do "import X;" instead of #include. CMake 3.28 added FILE_SET CXX_MODULES
    /// to handle the new compilation model — the compiler needs to build module interfaces in
    /// dependency order before anything that imports them. PUBLIC visibility means consumers
    /// of this library can also import these modules (which is the whole point of exporting them).
    ///
    /// Note: only Clang 16+, MSVC 17.4+, and GCC 14+ support this. CMake + Ninja is the most
    /// reliable generator combo for modules as of 2024.
    /// </summary>
    public CmakeBuilder AddModuleSources(params string[] files)
    {
        _moduleSources.AddRange(files);
        return this;
    }

    /// <summary>
    /// Emits: target_sources(name PUBLIC FILE_SET HEADERS FILES math.h ...)
    ///
    /// For traditional (non-module) libraries, public headers are the .h/.hpp files that
    /// consumers #include. FILE_SET HEADERS (CMake 3.23+) is the modern way to declare them —
    /// it replaces the old target_include_directories() dance and ensures headers get installed
    /// to the right location automatically. PUBLIC means both this target and its consumers
    /// can see these headers during compilation.
    ///
    /// You don't need this if you're using C++20 modules exclusively (use AddModuleSources).
    /// </summary>
    public CmakeBuilder AddPublicHeaders(params string[] files)
    {
        _publicHeaders.AddRange(files);
        return this;
    }

    /// <summary>
    /// Emits: find_package(name [CONFIG] [REQUIRED])
    ///
    /// find_package() searches for a library that's already installed on the system. When it
    /// succeeds, it makes a CMake target available (typically name::name) that you can link to.
    ///
    /// CONFIG mode means CMake looks for a name-config.cmake file that the library installed —
    /// this is the preferred modern approach because the library author defined exactly how to
    /// consume it. The fallback "Module" mode uses CMake's built-in FindXxx.cmake scripts, which
    /// are less reliable and often outdated.
    ///
    /// REQUIRED means CMake errors immediately if the package isn't found, instead of continuing
    /// and failing later with a cryptic linker error.
    ///
    /// After find_package(), you still need to call AddLinkLibrary() to actually link the target.
    /// </summary>
    /// <param name="packageName">The package name CMake searches for (case-sensitive on Linux).</param>
    /// <param name="required">If true, CMake aborts when the package isn't found.</param>
    /// <param name="configMode">If true, only look for name-config.cmake files (skip FindXxx.cmake).</param>
    public CmakeBuilder AddFindPackage(string packageName, bool required = true, bool configMode = false)
    {
        _findPackages.Add(new FindPackageDep(packageName, required, configMode));
        return this;
    }

    /// <summary>
    /// Emits: FetchContent_Declare(name GIT_REPOSITORY ... GIT_TAG ...) + FetchContent_MakeAvailable(name)
    ///
    /// FetchContent (CMake 3.11+) downloads a dependency's source code at configure time and
    /// builds it as part of your project. This is the modern alternative to git submodules or
    /// ExternalProject. The key advantage: the downloaded targets become first-class citizens
    /// in your build — you can link to them immediately with target_link_libraries().
    ///
    /// GIT_TAG should be a specific release tag (e.g. "v2.4.12") or commit hash, never a branch
    /// name, because branches move and your build would become non-reproducible.
    ///
    /// Tradeoff: this re-downloads and rebuilds from source every time you delete your build dir.
    /// For large deps, a system-level install via find_package() is faster for iteration.
    /// Abel uses FetchContent for small deps like doctest where build time is negligible.
    /// </summary>
    /// <param name="name">Logical name for CMake (used as the FetchContent identifier).</param>
    /// <param name="gitRepo">HTTPS URL of the git repository.</param>
    /// <param name="gitTag">Exact tag or commit hash to pin to.</param>
    public CmakeBuilder AddFetchContent(string name, string gitRepo, string? gitTag = null)
    {
        _fetchContents.Add(new FetchContentDep(name, gitRepo, gitTag));
        return this;
    }

    /// <summary>
    /// Emits: set(KEY VALUE CACHE BOOL "")
    ///
    /// Sets a CMake cache variable BEFORE FetchContent processes dependencies.
    /// This is how Abel controls third-party dependency build options
    /// (for example: SDL_SHARED OFF for static SDL builds).
    /// </summary>
    public CmakeBuilder AddCmakeOption(string key, string value)
    {
        _cmakeOptions.Add((key, value));
        return this;
    }

    public CmakeBuilder AddProjectCompileOptions(BuildCompilerOptionsConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);

        AddDistinctOptions(_projectCompileOptions.Common, options.Common);
        AddDistinctOptions(_projectCompileOptions.Msvc, options.Msvc);
        AddDistinctOptions(_projectCompileOptions.Gcc, options.Gcc);
        AddDistinctOptions(_projectCompileOptions.Clang, options.Clang);
        return this;
    }

    public CmakeBuilder AddProjectCompileOptionsForConfiguration(string configuration, BuildCompilerOptionsConfig options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var normalizedConfiguration = NormalizeBuildConfiguration(configuration);
        if (!_compileOptionsByConfiguration.TryGetValue(normalizedConfiguration, out var existing))
        {
            existing = new BuildCompilerOptionsSet();
            _compileOptionsByConfiguration[normalizedConfiguration] = existing;
        }

        AddDistinctOptions(existing.Common, options.Common);
        AddDistinctOptions(existing.Msvc, options.Msvc);
        AddDistinctOptions(existing.Gcc, options.Gcc);
        AddDistinctOptions(existing.Clang, options.Clang);
        return this;
    }

    public CmakeBuilder AddWrapperPackage(
        string name,
        string gitRepo,
        string gitTag,
        IEnumerable<string> sources,
        IEnumerable<string> includeDirs,
        IEnumerable<string> compileDefinitions,
        IEnumerable<string> cmakeTargets,
        IEnumerable<string> linkLibraries,
        bool interfaceOnly)
    {
        _wrapperPackages.Add(
            new WrapperPackageDep(
                name,
                gitRepo,
                gitTag,
                [.. sources],
                [.. includeDirs],
                [.. compileDefinitions],
                [.. cmakeTargets],
                [.. linkLibraries],
                interfaceOnly));

        return this;
    }

    /// <summary>
    /// Emits: target_link_libraries(name PRIVATE|PUBLIC dep1 dep2 ...)
    ///
    /// This is the single most important command in modern CMake. Linking a target does NOT just
    /// add -l flags to the linker — it transitively propagates all PUBLIC properties from the
    /// dependency: include paths, compile definitions, compile features, and further transitive
    /// dependencies. This is why modern CMake "just works" compared to manually wiring
    /// include_directories() and link_directories().
    ///
    /// Always use the namespaced target name (e.g. "math_module::math_module") rather than a raw
    /// library filename. Namespaced targets cause a clear CMake error if missing, while a raw
    /// name silently becomes a linker flag -lmath_module which produces a cryptic link error.
    ///
    /// Visibility is set automatically: PRIVATE for executables (nothing links to an exe),
    /// PUBLIC for libraries (consumers need to see transitive dependencies).
    /// </summary>
    /// <param name="target">The CMake target to link, e.g. "math_module::math_module".</param>
    public CmakeBuilder AddLinkLibrary(string target)
    {
        _linkLibraries.Add(target);
        return this;
    }

    /// <summary>
    /// Emits: include(CTest) + add_executable(testName ...) + add_test(NAME testName COMMAND testName)
    /// wrapped inside if(BUILD_TESTING) ... endif()
    ///
    /// CTest is CMake's built-in test runner. include(CTest) creates a BUILD_TESTING option
    /// (defaults ON) that lets users disable tests with -DBUILD_TESTING=OFF. Each test is a
    /// separate executable that returns 0 for pass, non-zero for fail. After building, you
    /// run them with "ctest --test-dir build" or "cmake --build build --target test".
    ///
    /// The test executable is linked PRIVATE to the main library target (so it can test it)
    /// plus any test framework targets like doctest::doctest.
    ///
    /// The if(BUILD_TESTING) guard means test dependencies (like doctest) are only downloaded
    /// and built when someone actually wants to run tests — consumers of your library skip them.
    /// </summary>
    /// <param name="testName">Name of the test executable and CTest test entry.</param>
    /// <param name="sources">Source files for the test executable.</param>
    /// <param name="linkLibraries">Additional libraries to link (e.g. test frameworks). The main project target is linked automatically.</param>
    public CmakeBuilder AddTest(string testName, IEnumerable<string> sources, IEnumerable<string>? linkLibraries = null)
    {
        _enableTesting = true;
        _testTargets.Add(new TestTarget(testName, [.. sources], [.. (linkLibraries ?? [])]));
        return this;
    }

    /// <summary>
    /// Emits: install(TARGETS ...) + install(EXPORT ...) + config file generation.
    ///
    /// "Installing" in CMake means copying your built artifacts (libraries, headers, module files)
    /// to a system-wide or prefix-local directory so OTHER projects can find them via find_package().
    ///
    /// This generates three things:
    ///   1. install(TARGETS) — copies the .a/.so and headers/modules to standard directories
    ///      using GNUInstallDirs (lib/, include/, etc.)
    ///   2. install(EXPORT) — creates a name-targets.cmake file that re-creates the CMake target
    ///      with all its properties, namespaced as name::name.
    ///   3. A name-config.cmake file — the entry point that find_package() looks for. It includes
    ///      the targets file so consumers get a ready-to-use target.
    ///
    /// After "cmake --install build", another project can do:
    ///   find_package(math_module CONFIG REQUIRED)
    ///   target_link_libraries(my_app PRIVATE math_module::math_module)
    /// and everything (includes, link flags, transitive deps) propagates automatically.
    ///
    /// Only meaningful for library targets — executables don't get consumed by other builds.
    /// </summary>
    public CmakeBuilder EnableInstall()
    {
        _enableInstall = true;
        return this;
    }

    public CmakeBuilder EnableLegacyHeaderSrcLayout()
    {
        _enableLegacyHeaderSrcLayout = true;
        return this;
    }

    // ─── Build ───────────────────────────────────────────────────────

    public string Build()
    {
        if (string.IsNullOrWhiteSpace(_projectName))
            throw new InvalidOperationException("Project name is required. Call SetProject() first.");

        if (_outputType == OutputType.library &&
            _privateSources.Count == 0 &&
            _moduleSources.Count == 0 &&
            !_enableLegacyHeaderSrcLayout)
            throw new InvalidOperationException(
                $"Library '{_projectName}' has no source files. Add module sources or private sources.");

        var w = new CmakeWriter();

        WritePreamble(w);
        WriteCmakeOptions(w);
        WriteFetchContent(w);
        WriteWrapperPackages(w);
        WriteFindPackages(w);
        WriteTarget(w);
        WriteLegacyHeaderSrcSupport(w);
        WriteSources(w);
        WriteCompileFeatures(w);
        WriteDefaultCompilerFlags(w);
        WriteProjectCompilerFlags(w);
        WriteLinkLibraries(w);

        if (_outputType == OutputType.library && _enableInstall)
            WriteInstallRules(w);

        if (_enableTesting)
            WriteTests(w);

        return w.ToString();
    }

    // ─── Convenience factory: build from ProjectConfig ───────────────

    /// <summary>
    /// Creates a pre-configured builder from a project.json config.
    ///
    /// Mapping:
    ///   config.Name             → project() name and target name
    ///   config.CXXStandard      → CMAKE_CXX_STANDARD + target_compile_features
    ///   config.ProjectOutputType → add_executable() vs add_library(STATIC)
    ///   config.Sources["modules"]  → FILE_SET CXX_MODULES (C++20 module interfaces)
    ///   config.Sources["private"]  → PRIVATE sources (implementation .cpp files)
    ///   config.Sources["public"]   → FILE_SET HEADERS (traditional .h/.hpp)
    ///   config.Dependencies      → find_package(dep CONFIG REQUIRED) + target_link_libraries(dep::dep)
    ///   config.Tests.Files       → CTest executables, auto-fetches doctest via FetchContent
    ///
    /// Libraries automatically get install/export rules so they can be consumed
    /// by other Abel projects via find_package() after "cmake --install build".
    /// </summary>
    public static CmakeBuilder FromProjectConfig(ProjectConfig config, PackageRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var builder = new CmakeBuilder()
            .SetProject(config.Name)
            .SetCxxStandard(config.CXXStandard)
            .SetOutputType(config.ProjectOutputType);

        // Map sources dictionary
        if (config.Sources.TryGetValue("modules", out var modules))
            builder.AddModuleSources(modules);

        if (config.Sources.TryGetValue("private", out var privateSrc))
            builder.AddPrivateSources(privateSrc);

        if (config.Sources.TryGetValue("public", out var publicHdrs))
            builder.AddPublicHeaders(publicHdrs);

        if (config.Build is not null)
        {
            if (config.Build.LegacyHeaderSrcLayout)
                builder.EnableLegacyHeaderSrcLayout();

            builder.AddProjectCompileOptions(config.Build.CompileOptions);

            foreach (var configuration in config.Build.Configurations)
            {
                if (configuration.Value is null)
                    continue;

                builder.AddProjectCompileOptionsForConfiguration(configuration.Key, configuration.Value.CompileOptions);
            }
        }

        var resolvedPackageVariants = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var dependencyText in config.Dependencies)
        {
            var dependencySpec = ProjectDependencySpec.Parse(dependencyText);

            if (dependencySpec.IsGit)
            {
                builder.AddFindPackage(dependencySpec.Name, required: true, configMode: true);
                builder.AddLinkLibrary($"{dependencySpec.Name}::{dependencySpec.Name}");
                continue;
            }

            var package = registry?.Find(dependencySpec.Name);

            if (package is not null && registry is not null)
            {
                ResolvePackageTree(
                    package,
                    dependencySpec.VariantName,
                    registry,
                    builder,
                    resolvedPackageVariants);
            }
            else
            {
                if (dependencySpec.VariantName is not null)
                {
                    throw new InvalidOperationException(
                        $"Unknown package '{dependencySpec.Name}' in dependency '{dependencyText}'. " +
                        "Variant syntax is only supported for registry packages.");
                }

                // Unknown to registry: either local Abel package or system package.
                builder.AddFindPackage(dependencySpec.Name, required: true, configMode: true);
                builder.AddLinkLibrary($"{dependencySpec.Name}::{dependencySpec.Name}");
            }
        }

        // If library, enable install by default
        if (config.ProjectOutputType == OutputType.library)
            builder.EnableInstall();

        // Tests
        if (config.Tests.Files.Count > 0)
        {
            // Auto-fetch doctest
            builder.AddFetchContent(
                "doctest",
                "https://github.com/doctest/doctest.git",
                "v2.4.12"
            );

            foreach (var testFile in config.Tests.Files)
            {
                var testName = Path.GetFileNameWithoutExtension(testFile);
                builder.AddTest(
                    testName,
                    [testFile],
                    ["doctest::doctest"]
                );
            }
        }

        return builder;
    }

    private static void ResolvePackageTree(
        PackageEntry package,
        string? variantName,
        PackageRegistry registry,
        CmakeBuilder builder,
        Dictionary<string, string?> resolvedPackageVariants)
    {
        if (resolvedPackageVariants.TryGetValue(package.Name, out var existingVariant))
        {
            if (!string.Equals(existingVariant, variantName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Package '{package.Name}' was requested with multiple variants: " +
                    $"'{existingVariant ?? "<none>"}' and '{variantName ?? "<none>"}'.");
            }

            return;
        }

        var variant = GetVariant(package, variantName);
        resolvedPackageVariants[package.Name] = variantName;

        var transitiveDependencyNames = MergeDistinctStrings(
            package.Dependencies,
            variant?.Dependencies);

        foreach (var transitiveDependencyText in transitiveDependencyNames)
        {
            var transitiveSpec = DependencySpec.Parse(transitiveDependencyText);
            var transitivePackage = registry.Find(transitiveSpec.PackageName);

            if (transitivePackage is null)
            {
                throw new InvalidOperationException(
                    $"Registry package '{package.Name}' depends on '{transitiveDependencyText}', but it is not registered.");
            }

            ResolvePackageTree(
                transitivePackage,
                transitiveSpec.VariantName,
                registry,
                builder,
                resolvedPackageVariants);
        }

        foreach (var option in package.CmakeOptions)
            builder.AddCmakeOption(option.Key, option.Value);

        var strategy = package.Strategy.Trim();
        if (strategy.Equals("fetchcontent", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddFetchContent(package.Name, package.GitRepository, package.GitTag);
        }
        else if (strategy.Equals("wrapper", StringComparison.OrdinalIgnoreCase))
        {
            var sources = MergeDistinctStrings(package.Sources, variant?.Sources);
            var includeDirs = MergeDistinctStrings(package.IncludeDirs, variant?.IncludeDirs);
            var compileDefinitions = MergeDistinctStrings(package.CompileDefinitions, variant?.CompileDefinitions);
            var linkLibraries = CollectTransitiveLinkTargets(transitiveDependencyNames, registry);

            if (sources.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Package '{package.Name}' uses wrapper strategy but does not define sources.");
            }

            builder.AddWrapperPackage(
                package.Name,
                package.GitRepository,
                package.GitTag,
                sources,
                includeDirs,
                compileDefinitions,
                package.CmakeTargets,
                linkLibraries,
                interfaceOnly: false);
        }
        else if (strategy.Equals("header_inject", StringComparison.OrdinalIgnoreCase))
        {
            var includeDirs = MergeDistinctStrings(package.IncludeDirs, variant?.IncludeDirs);
            var compileDefinitions = MergeDistinctStrings(package.CompileDefinitions, variant?.CompileDefinitions);
            var linkLibraries = CollectTransitiveLinkTargets(transitiveDependencyNames, registry);

            builder.AddWrapperPackage(
                package.Name,
                package.GitRepository,
                package.GitTag,
                Array.Empty<string>(),
                includeDirs,
                compileDefinitions,
                package.CmakeTargets,
                linkLibraries,
                interfaceOnly: true);
        }
        else
        {
            throw new InvalidOperationException(
                $"Package '{package.Name}' has unsupported strategy '{package.Strategy}'.");
        }

        foreach (var cmakeTarget in package.CmakeTargets)
            builder.AddLinkLibrary(cmakeTarget);
    }

    private static PackageVariant? GetVariant(PackageEntry package, string? variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName))
            return null;

        if (!package.Variants.TryGetValue(variantName, out var variant))
        {
            throw new InvalidOperationException(
                $"Package '{package.Name}' does not define variant '{variantName}'.");
        }

        return variant;
    }

    private static List<string> MergeDistinctStrings(
        IEnumerable<string>? first,
        IEnumerable<string>? second)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in first ?? [])
        {
            if (seen.Add(entry))
                output.Add(entry);
        }

        foreach (var entry in second ?? [])
        {
            if (seen.Add(entry))
                output.Add(entry);
        }

        return output;
    }

    private static void AddDistinctOptions(List<string> destination, IEnumerable<string> source)
    {
        foreach (var option in source)
        {
            if (string.IsNullOrWhiteSpace(option))
                continue;

            if (!destination.Contains(option, StringComparer.Ordinal))
                destination.Add(option);
        }
    }

    private static string NormalizeBuildConfiguration(string configuration)
    {
        if (configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            return "Debug";
        if (configuration.Equals("Release", StringComparison.OrdinalIgnoreCase))
            return "Release";
        if (configuration.Equals("RelWithDebInfo", StringComparison.OrdinalIgnoreCase))
            return "RelWithDebInfo";
        if (configuration.Equals("MinSizeRel", StringComparison.OrdinalIgnoreCase))
            return "MinSizeRel";

        throw new InvalidOperationException(
            $"Unsupported build configuration '{configuration}' in project.json. Use Debug, Release, RelWithDebInfo, or MinSizeRel.");
    }

    private static List<string> CollectTransitiveLinkTargets(
        IEnumerable<string> transitiveDependencyNames,
        PackageRegistry registry)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var transitiveDependencyText in transitiveDependencyNames)
        {
            var transitiveSpec = DependencySpec.Parse(transitiveDependencyText);
            var transitivePackage = registry.Find(transitiveSpec.PackageName);
            if (transitivePackage is null)
                continue;

            foreach (var cmakeTarget in transitivePackage.CmakeTargets)
            {
                if (seen.Add(cmakeTarget))
                    targets.Add(cmakeTarget);
            }
        }

        return targets;
    }

    // ─── Private write helpers ───────────────────────────────────────

    /// <summary>
    /// The preamble sets global project defaults. CMAKE_CXX_STANDARD as a variable is a fallback
    /// for targets that don't explicitly set compile features — it's the "floor" standard.
    /// CMAKE_CXX_EXTENSIONS OFF avoids GNU extensions (-std=gnu++23 → -std=c++23) for portability.
    /// </summary>
    private void WritePreamble(CmakeWriter w)
    {
        w.Line($"cmake_minimum_required(VERSION {_cmakeMinVersion})");
        w.Line($"project({_projectName} LANGUAGES {_language})");
        w.Blank();
        w.Line($"set(CMAKE_CXX_STANDARD {_cxxStandard})");
        w.Line("set(CMAKE_CXX_STANDARD_REQUIRED ON)");
        w.Line("set(CMAKE_CXX_EXTENSIONS OFF)");
    }

    private void WriteCmakeOptions(CmakeWriter w)
    {
        if (_cmakeOptions.Count == 0) return;

        w.Blank();
        w.Line("# Dependency build options");
        foreach (var (key, value) in _cmakeOptions)
            w.Line($"set({key} {value} CACHE BOOL \"\")");
    }

    /// <summary>
    /// FetchContent is declared first (before find_package) because fetched deps may provide
    /// targets that find_package would otherwise fail to locate. FetchContent_MakeAvailable()
    /// calls FetchContent_Populate() + add_subdirectory() internally, making all targets from
    /// the fetched project available immediately.
    /// </summary>
    private void WriteFetchContent(CmakeWriter w)
    {
        if (_fetchContents.Count == 0) return;

        w.Blank();
        w.Line("include(FetchContent)");

        foreach (var fc in _fetchContents)
        {
            w.Line("FetchContent_Declare(");
            w.Line($"    {fc.Name}");
            w.Line($"    GIT_REPOSITORY {EscapeCmakeArgument(fc.GitRepo)}");
            if (!string.IsNullOrWhiteSpace(fc.GitTag))
                w.Line($"    GIT_TAG        {EscapeCmakeArgument(fc.GitTag)}");
            w.Line("    GIT_SHALLOW    TRUE");
            w.Line(")");
        }

        var names = string.Join(" ", _fetchContents.Select(fc => fc.Name));
        w.Line($"FetchContent_MakeAvailable({names})");
    }

    private void WriteWrapperPackages(CmakeWriter w)
    {
        if (_wrapperPackages.Count == 0) return;

        if (_fetchContents.Count == 0)
        {
            w.Blank();
            w.Line("include(FetchContent)");
        }

        foreach (var package in _wrapperPackages)
        {
            var internalTarget = $"{ToSafeTargetIdentifier(package.Name)}_lib";

            w.Blank();
            w.Line($"FetchContent_Declare(");
            w.Line($"    {package.Name}");
            w.Line($"    GIT_REPOSITORY {package.GitRepo}");
            w.Line($"    GIT_TAG        {package.GitTag}");
            w.Line($"    GIT_SHALLOW    TRUE");
            w.Line(")");
            w.Line($"FetchContent_GetProperties({package.Name})");
            w.Line($"if(NOT {package.Name}_POPULATED)");
            w.Line($"    FetchContent_Populate({package.Name})");
            w.Line("endif()");
            w.Blank();

            if (package.InterfaceOnly)
            {
                w.Line($"add_library({internalTarget} INTERFACE)");

                if (package.IncludeDirs.Count > 0)
                {
                    w.Line($"target_include_directories({internalTarget} INTERFACE");
                    foreach (var includeDir in package.IncludeDirs)
                        w.Line($"    {AsSourceDir(package.Name, includeDir)}");
                    w.Line(")");
                }

                if (package.CompileDefinitions.Count > 0)
                {
                    w.Line($"target_compile_definitions({internalTarget} INTERFACE");
                    foreach (var definition in package.CompileDefinitions)
                        w.Line($"    {definition}");
                    w.Line(")");
                }

                if (package.LinkLibraries.Count > 0)
                {
                    w.Line($"target_link_libraries({internalTarget} INTERFACE");
                    foreach (var dependencyTarget in package.LinkLibraries)
                        w.Line($"    {dependencyTarget}");
                    w.Line(")");
                }
            }
            else
            {
                w.Line($"add_library({internalTarget} STATIC)");

                if (package.Sources.Count > 0)
                {
                    w.Line($"target_sources({internalTarget} PRIVATE");
                    foreach (var source in package.Sources)
                        w.Line($"    {AsSourceDir(package.Name, source)}");
                    w.Line(")");
                }

                if (package.IncludeDirs.Count > 0)
                {
                    w.Line($"target_include_directories({internalTarget} PUBLIC");
                    foreach (var includeDir in package.IncludeDirs)
                        w.Line($"    {AsSourceDir(package.Name, includeDir)}");
                    w.Line(")");
                }

                if (package.CompileDefinitions.Count > 0)
                {
                    w.Line($"target_compile_definitions({internalTarget} PUBLIC");
                    foreach (var definition in package.CompileDefinitions)
                        w.Line($"    {definition}");
                    w.Line(")");
                }

                if (package.LinkLibraries.Count > 0)
                {
                    w.Line($"target_link_libraries({internalTarget} PUBLIC");
                    foreach (var dependencyTarget in package.LinkLibraries)
                        w.Line($"    {dependencyTarget}");
                    w.Line(")");
                }
            }

            foreach (var aliasTarget in package.CmakeTargets)
                w.Line($"add_library({aliasTarget} ALIAS {internalTarget})");
        }
    }

    private static string ToSafeTargetIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "package";

        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray();
        var identifier = new string(chars);
        if (char.IsDigit(identifier[0]))
            return $"pkg_{identifier}";
        return identifier;
    }

    private static string AsSourceDir(string packageName, string relativePath)
    {
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
            return $"${{{packageName}_SOURCE_DIR}}";
        return $"${{{packageName}_SOURCE_DIR}}/{relativePath}";
    }

    private void WriteFindPackages(CmakeWriter w)
    {
        if (_findPackages.Count == 0) return;

        w.Blank();
        foreach (var pkg in _findPackages)
        {
            var parts = new List<string> { pkg.Name };
            if (pkg.ConfigMode) parts.Add("CONFIG");
            if (pkg.Required) parts.Add("REQUIRED");
            w.Line($"find_package({string.Join(" ", parts)})");
        }
    }

    private void WriteTarget(CmakeWriter w)
    {
        w.Blank();
        if (_outputType == OutputType.exe)
        {
            w.Line($"add_executable({_projectName})");
        }
        else
        {
            w.Line($"add_library({_projectName} STATIC)");
        }
    }

    private void WriteLegacyHeaderSrcSupport(CmakeWriter w)
    {
        if (!_enableLegacyHeaderSrcLayout)
            return;

        w.Blank();
        w.Line("# Legacy header/src support (non-module layout).");
        var includeScope = _outputType == OutputType.library ? "PUBLIC" : "PRIVATE";
        w.Line("if(EXISTS \"${CMAKE_CURRENT_SOURCE_DIR}/include\")");
        w.Line($"    target_include_directories({_projectName} {includeScope}");
        w.Line("        \"${CMAKE_CURRENT_SOURCE_DIR}/include\"");
        w.Line("    )");
        w.Line("endif()");

        if (_privateSources.Count > 0 || _moduleSources.Count > 0 || _publicHeaders.Count > 0)
            return;

        var legacySourcesVariable = BuildLegacySourcesVariableName();
        w.Line($"file(GLOB_RECURSE {legacySourcesVariable} CONFIGURE_DEPENDS");
        w.Line("    \"${CMAKE_CURRENT_SOURCE_DIR}/src/*.c\"");
        w.Line("    \"${CMAKE_CURRENT_SOURCE_DIR}/src/*.cc\"");
        w.Line("    \"${CMAKE_CURRENT_SOURCE_DIR}/src/*.cxx\"");
        w.Line("    \"${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp\"");
        w.Line(")");
        w.Line($"if({legacySourcesVariable})");
        w.Line($"    target_sources({_projectName} PRIVATE ${{{legacySourcesVariable}}})");
        w.Line("endif()");
    }

    private string BuildLegacySourcesVariableName()
    {
        var prefix = ToSafeTargetIdentifier(_projectName).ToUpperInvariant();
        return $"ABEL_{prefix}_LEGACY_SOURCES";
    }

    private void WriteSources(CmakeWriter w)
    {
        // Executables with no explicit sources default to main.cpp — the sane Abel convention.
        // Libraries must have at least one source (module or private), otherwise there's nothing to compile.
        if (_outputType == OutputType.exe && _privateSources.Count == 0 && _moduleSources.Count == 0)
            _privateSources.Add("main.cpp");

        bool hasSources = _moduleSources.Count > 0 || _privateSources.Count > 0 || _publicHeaders.Count > 0;
        if (!hasSources) return;

        w.Line($"target_sources({_projectName}");

        if (_moduleSources.Count > 0)
        {
            w.Line("    PUBLIC FILE_SET CXX_MODULES FILES");
            foreach (var src in _moduleSources)
                w.Line($"        {src}");
        }

        if (_publicHeaders.Count > 0)
        {
            w.Line("    PUBLIC FILE_SET HEADERS FILES");
            foreach (var hdr in _publicHeaders)
                w.Line($"        {hdr}");
        }

        if (_privateSources.Count > 0)
        {
            w.Line("    PRIVATE");
            foreach (var src in _privateSources)
                w.Line($"        {src}");
        }

        w.Line(")");
    }

    /// <summary>
    /// target_compile_features() is the modern way to express "this target requires C++23".
    /// Unlike the CMAKE_CXX_STANDARD variable, this propagates through target_link_libraries:
    /// if library A requires C++23 and executable B links to A, B automatically compiles with
    /// at least C++23. We set PUBLIC for libraries so this propagation happens, and skip it
    /// for executables since nothing links to them.
    /// </summary>
    private void WriteCompileFeatures(CmakeWriter w)
    {
        // Use target_compile_features rather than global variables for
        // propagation — the modern CMake way to express standard requirements.
        if (_outputType == OutputType.library)
        {
            w.Line($"target_compile_features({_projectName} PUBLIC cxx_std_{_cxxStandard})");
        }
    }

    private void WriteDefaultCompilerFlags(CmakeWriter w)
    {
        w.Blank();
        w.Line("# Sane warning defaults for project sources.");
        w.Line("if(MSVC)");
        w.Line($"    target_compile_options({_projectName} PRIVATE /W4 /permissive-)");
        w.Line("else()");
        w.Line($"    target_compile_options({_projectName} PRIVATE -Wall -Wextra -Wpedantic)");
        w.Line("endif()");
    }

    private void WriteProjectCompilerFlags(CmakeWriter w)
    {
        if (!HasProjectCompilerFlags())
            return;

        w.Blank();
        w.Line("# Extra compiler options from project.json build section.");
        WriteProjectCompilerOptionsBlock(w, _projectCompileOptions, configuration: null);

        foreach (var configuration in _compileOptionsByConfiguration.OrderBy(item => item.Key, StringComparer.Ordinal))
            WriteProjectCompilerOptionsBlock(w, configuration.Value, configuration.Key);
    }

    private bool HasProjectCompilerFlags()
    {
        if (HasCompilerOptions(_projectCompileOptions))
            return true;

        foreach (var options in _compileOptionsByConfiguration.Values)
        {
            if (HasCompilerOptions(options))
                return true;
        }

        return false;
    }

    private void WriteProjectCompilerOptionsBlock(
        CmakeWriter w,
        BuildCompilerOptionsSet options,
        string? configuration)
    {
        var compileOptions = BuildCompileOptions(options, configuration);
        if (compileOptions.Count == 0)
            return;

        w.Line($"target_compile_options({_projectName} PRIVATE");
        foreach (var option in compileOptions)
            w.Line($"    {option}");
        w.Line(")");
    }

    private static List<string> BuildCompileOptions(BuildCompilerOptionsSet options, string? configuration)
    {
        var output = new List<string>();
        AddCompilerOptions(output, options.Common, configuration, Array.Empty<string>());
        AddCompilerOptions(output, options.Msvc, configuration, "MSVC");
        AddCompilerOptions(output, options.Gcc, configuration, "GNU");
        AddCompilerOptions(output, options.Clang, configuration, "Clang", "AppleClang");
        return output;
    }

    private static void AddCompilerOptions(
        List<string> output,
        IEnumerable<string> options,
        string? configuration,
        params string[] compilerIds)
    {
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option))
                continue;

            var escapedOption = EscapeCmakeArgument(option);
            var expression = BuildCompilerExpression(escapedOption, configuration, compilerIds);
            output.Add(expression);
        }
    }

    private static string BuildCompilerExpression(string option, string? configuration, params string[] compilerIds)
    {
        if (string.IsNullOrWhiteSpace(configuration) && compilerIds.Length == 0)
            return option;

        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuration))
            conditions.Add($"$<CONFIG:{configuration}>");

        if (compilerIds.Length > 0)
            conditions.Add($"$<CXX_COMPILER_ID:{string.Join(",", compilerIds)}>");

        if (conditions.Count == 1)
            return $"$<{conditions[0]}:{option}>";

        return $"$<$<AND:{string.Join(",", conditions)}>:{option}>";
    }

    private static string EscapeCmakeArgument(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return escaped.IndexOfAny([' ', ';']) >= 0 ? $"\"{escaped}\"" : escaped;
    }

    private static bool HasCompilerOptions(BuildCompilerOptionsSet options) =>
        options.Common.Count > 0 ||
        options.Msvc.Count > 0 ||
        options.Gcc.Count > 0 ||
        options.Clang.Count > 0;

    /// <summary>
    /// For executables: PRIVATE because nothing further links to an exe.
    /// For libraries: PUBLIC because if library A depends on library B, then anything
    /// linking to A also needs B's symbols and include paths transitively.
    /// </summary>
    private void WriteLinkLibraries(CmakeWriter w)
    {
        if (_linkLibraries.Count == 0) return;

        var scope = _outputType == OutputType.exe ? "PRIVATE" : "PUBLIC";
        w.Line($"target_link_libraries({_projectName} {scope}");
        foreach (var lib in _linkLibraries)
            w.Line($"    {lib}");
        w.Line(")");
    }

    /// <summary>
    /// Install rules use GNUInstallDirs for cross-platform directory conventions:
    ///   ${CMAKE_INSTALL_LIBDIR}     → lib/ or lib64/ depending on distro
    ///   ${CMAKE_INSTALL_INCLUDEDIR} → include/
    ///
    /// The export chain works like this:
    ///   install(TARGETS ... EXPORT name-targets) — tells CMake to record this target's properties
    ///   install(EXPORT name-targets)             — writes name-targets.cmake that recreates the target
    ///   name-config.cmake                        — entry point file that find_package() searches for
    ///
    /// The config file uses CMakeFindDependencyMacro so that transitive find_package() calls
    /// happen automatically when a consumer does find_package(name).
    /// </summary>
    private void WriteInstallRules(CmakeWriter w)
    {
        w.Blank();
        w.Line("include(GNUInstallDirs)");
        w.Blank();

        // Install the library target
        w.Line($"install(TARGETS {_projectName}");
        w.Line($"    EXPORT {_projectName}-targets");
        w.Line("    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}");

        if (_moduleSources.Count > 0)
        {
            w.Line("    FILE_SET CXX_MODULES");
            w.Line($"        DESTINATION ${{CMAKE_INSTALL_LIBDIR}}/cmake/{_projectName}/cxx_modules");
        }

        if (_publicHeaders.Count > 0)
        {
            w.Line("    FILE_SET HEADERS");
            w.Line("        DESTINATION ${CMAKE_INSTALL_INCLUDEDIR}");
        }

        w.Line(")");

        // Export targets
        w.Blank();
        w.Line($"install(EXPORT {_projectName}-targets");
        w.Line($"    NAMESPACE {_projectName}::");
        w.Line($"    DESTINATION ${{CMAKE_INSTALL_LIBDIR}}/cmake/{_projectName}");
        w.Line(")");

        // Generate config file
        w.Blank();
        w.Line("include(CMakePackageConfigHelpers)");
        w.Blank();

        w.Line($"file(WRITE \"${{CMAKE_CURRENT_BINARY_DIR}}/{_projectName}-config.cmake\" [=[");
        w.Line("include(CMakeFindDependencyMacro)");

        foreach (var dependencyName in _findPackages
                     .Select(p => p.Name)
                     .Distinct(StringComparer.Ordinal))
        {
            w.Line($"find_dependency({dependencyName} CONFIG REQUIRED)");
        }

        w.Line($"include(\"${{CMAKE_CURRENT_LIST_DIR}}/{_projectName}-targets.cmake\")");
        w.Line("]=])");
        w.Blank();
        w.Line($"install(FILES \"${{CMAKE_CURRENT_BINARY_DIR}}/{_projectName}-config.cmake\"");
        w.Line($"    DESTINATION ${{CMAKE_INSTALL_LIBDIR}}/cmake/{_projectName}");
        w.Line(")");
    }

    /// <summary>
    /// Tests are guarded by if(BUILD_TESTING) so that consumers who add this project
    /// via FetchContent or add_subdirectory don't waste time building and linking tests
    /// they'll never run. Users opt out with: cmake -DBUILD_TESTING=OFF
    /// </summary>
    private void WriteTests(CmakeWriter w)
    {
        w.Blank();
        w.Line("include(CTest)");
        w.Blank();
        w.Line("if(BUILD_TESTING)");

        foreach (var test in _testTargets)
        {
            var sources = string.Join(" ", test.Sources);
            w.Line($"    add_executable({test.Name} {sources})");

            // Link the main target + any test-specific libs (e.g. doctest)
            var allLibs = new List<string> { _projectName };
            allLibs.AddRange(test.LinkLibraries);

            w.Line($"    target_link_libraries({test.Name} PRIVATE");
            foreach (var lib in allLibs)
                w.Line($"        {lib}");
            w.Line("    )");

            w.Line($"    add_test(NAME {test.Name} COMMAND {test.Name})");
        }

        w.Line("endif()");
    }

    // ─── Internal types ──────────────────────────────────────────────

    private record FindPackageDep(string Name, bool Required, bool ConfigMode);
    private record FetchContentDep(string Name, string GitRepo, string? GitTag);
    private record WrapperPackageDep(
        string Name,
        string GitRepo,
        string GitTag,
        List<string> Sources,
        List<string> IncludeDirs,
        List<string> CompileDefinitions,
        List<string> CmakeTargets,
        List<string> LinkLibraries,
        bool InterfaceOnly);
    private record TestTarget(string Name, List<string> Sources, List<string> LinkLibraries);
    private sealed class BuildCompilerOptionsSet
    {
        public List<string> Common { get; } = [];
        public List<string> Msvc { get; } = [];
        public List<string> Gcc { get; } = [];
        public List<string> Clang { get; } = [];
    }
}

/// <summary>
/// Tiny helper that builds up the CMake script line-by-line,
/// keeping indentation and blank-line logic in one place.
/// </summary>
internal class CmakeWriter
{
    private readonly List<string> _lines = [];

    public void Line(string text) => _lines.Add(text);
    public void Blank() => _lines.Add("");

    public override string ToString() => string.Join("\n", _lines) + "\n";
}
