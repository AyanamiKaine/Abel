using System.Text.Json;

namespace Abel.Core;

/// <summary>
/// Curated third-party package registry used for dependency resolution.
/// </summary>
public class PackageRegistry
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;
    private readonly Dictionary<string, PackageEntry> _packages = new(KeyComparer);
    private readonly Dictionary<string, string> _aliases = new(KeyComparer);

    public PackageRegistry()
    {
        LoadBuiltinPackages();

        // User-level override/extension.
        var homeRegistryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".abel",
            "registry.json");

        LoadFromFile(homeRegistryPath);
        LoadFromFile(Path.Combine(Environment.CurrentDirectory, "abel-registry.json"));
    }

    public PackageEntry? Find(string name)
    {
        var resolved = ResolvePackageName(name);
        return _packages.TryGetValue(resolved, out var entry) ? entry : null;
    }

    public bool IsKnownPackage(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var parsed = DependencySpec.Parse(name);
        return _packages.ContainsKey(ResolvePackageName(parsed.PackageName));
    }

    public IEnumerable<PackageEntry> All => _packages.Values;

    public void LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<PackageEntry>>(json);

        if (entries is null)
            return;

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
                _packages[entry.Name] = entry;
        }
    }

    public void Register(PackageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Name);
        _packages[entry.Name] = entry;
    }

    private void RegisterAlias(string alias, string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        _aliases[alias] = packageName;
    }

    private string ResolvePackageName(string packageName) =>
        _aliases.TryGetValue(packageName, out var canonicalName) ? canonicalName : packageName;

    private void LoadBuiltinPackages()
    {
        Register(new PackageEntry
        {
            Name = "sdl3",
            GitRepository = "https://github.com/libsdl-org/SDL.git",
            GitTag = "release-3.4.2",
            Strategy = "fetchcontent",
            CmakeTargets = ["SDL3::SDL3"],
            CmakeOptions = new Dictionary<string, string>(KeyComparer)
            {
                ["SDL_SHARED"] = "OFF",
                ["SDL_STATIC"] = "ON",
                ["SDL_TEST_LIBRARY"] = "OFF",
                ["SDL_TESTS"] = "OFF",
                ["SDL_EXAMPLES"] = "OFF",
            },
            Description = "Cross-platform multimedia library (graphics, audio, input)",
        });

        Register(new PackageEntry
        {
            Name = "sdl3_image",
            GitRepository = "https://github.com/libsdl-org/SDL_image.git",
            GitTag = "release-3.2.4",
            Strategy = "fetchcontent",
            CmakeTargets = ["SDL3_image::SDL3_image"],
            CmakeOptions = new Dictionary<string, string>(KeyComparer)
            {
                ["SDL3IMAGE_SAMPLES"] = "OFF",
                ["SDL3IMAGE_TESTS"] = "OFF",
            },
            Dependencies = ["sdl3"],
            Description = "Image loading library for SDL3 (PNG, JPG, WebP, etc.)",
        });

        Register(new PackageEntry
        {
            Name = "fmt",
            GitRepository = "https://github.com/fmtlib/fmt.git",
            GitTag = "11.1.4",
            Strategy = "fetchcontent",
            CmakeTargets = ["fmt::fmt"],
            CmakeOptions = new Dictionary<string, string>(KeyComparer)
            {
                ["FMT_DOC"] = "OFF",
                ["FMT_TEST"] = "OFF",
            },
            Description = "Fast, safe C++ formatting library",
        });

        Register(new PackageEntry
        {
            Name = "spdlog",
            GitRepository = "https://github.com/gabime/spdlog.git",
            GitTag = "v1.15.1",
            Strategy = "fetchcontent",
            CmakeTargets = ["spdlog::spdlog"],
            CmakeOptions = new Dictionary<string, string>(KeyComparer)
            {
                ["SPDLOG_FMT_EXTERNAL"] = "ON",
                ["SPDLOG_BUILD_BENCH"] = "OFF",
                ["SPDLOG_BUILD_TESTS"] = "OFF",
                ["SPDLOG_BUILD_EXAMPLE"] = "OFF",
            },
            Dependencies = ["fmt"],
            Description = "Fast C++ logging library",
        });

        Register(new PackageEntry
        {
            Name = "fmtlog",
            GitRepository = "https://github.com/MengRao/fmtlog.git",
            GitTag = "v2.3.0",
            Strategy = "wrapper",
            CmakeTargets = ["fmtlog::fmtlog"],
            Sources = ["fmtlog.cc"],
            IncludeDirs = ["."],
            Dependencies = ["fmt"],
            Description = "High-performance asynchronous logging library built on fmt",
        });

        Register(new PackageEntry
        {
            Name = "flecs",
            GitRepository = "https://github.com/SanderMertens/flecs.git",
            GitTag = "v4.1.4",
            Strategy = "fetchcontent",
            CmakeTargets = ["flecs::flecs_static"],
            CmakeOptions = new Dictionary<string, string>(KeyComparer)
            {
                ["FLECS_STATIC"] = "ON",
                ["FLECS_SHARED"] = "OFF",
                ["FLECS_TESTS"] = "OFF",
                ["FLECS_EXAMPLES"] = "OFF",
            },
            Description = "Fast entity component system",
        });

        Register(new PackageEntry
        {
            Name = "imgui",
            GitRepository = "https://github.com/ocornut/imgui.git",
            GitTag = "docking",
            Strategy = "wrapper",
            CmakeTargets = ["imgui::imgui"],
            Sources =
            [
                "imgui.cpp",
                "imgui_draw.cpp",
                "imgui_tables.cpp",
                "imgui_widgets.cpp",
                "imgui_demo.cpp",
            ],
            IncludeDirs = ["."],
            Variants = new Dictionary<string, PackageVariant>(KeyComparer)
            {
                ["sdl3_renderer"] = new PackageVariant
                {
                    Sources =
                    [
                        "backends/imgui_impl_sdl3.cpp",
                        "backends/imgui_impl_sdlrenderer3.cpp",
                    ],
                    IncludeDirs = ["backends"],
                    Dependencies = ["sdl3"],
                },
                ["sdl3_opengl3"] = new PackageVariant
                {
                    Sources =
                    [
                        "backends/imgui_impl_sdl3.cpp",
                        "backends/imgui_impl_opengl3.cpp",
                    ],
                    IncludeDirs = ["backends"],
                    Dependencies = ["sdl3"],
                },
                ["sdl3_vulkan"] = new PackageVariant
                {
                    Sources =
                    [
                        "backends/imgui_impl_sdl3.cpp",
                        "backends/imgui_impl_vulkan.cpp",
                    ],
                    IncludeDirs = ["backends"],
                    Dependencies = ["sdl3"],
                },
            },
            Description = "Immediate-mode GUI library",
        });

        Register(new PackageEntry
        {
            Name = "stb",
            GitRepository = "https://github.com/nothings/stb.git",
            GitTag = "f0569113a9342d9cf8d7c74942a8f6f0f684995",
            Strategy = "header_inject",
            CmakeTargets = ["stb::stb"],
            IncludeDirs = ["."],
            Description = "Single-header libraries (stb_image, stb_truetype, etc.)",
        });

        Register(new PackageEntry
        {
            Name = "glm",
            GitRepository = "https://github.com/g-truc/glm.git",
            GitTag = "1.0.1",
            Strategy = "fetchcontent",
            CmakeTargets = ["glm::glm"],
            CmakeOptions = new Dictionary<string, string>(KeyComparer)
            {
                ["GLM_BUILD_TESTS"] = "OFF",
            },
            Description = "OpenGL Mathematics library (vectors, matrices, quaternions)",
        });

        Register(new PackageEntry
        {
            Name = "nlohmann_json",
            GitRepository = "https://github.com/nlohmann/json.git",
            GitTag = "v3.12.0",
            Strategy = "fetchcontent",
            CmakeTargets = ["nlohmann_json::nlohmann_json"],
            CmakeOptions = new Dictionary<string, string>(KeyComparer)
            {
                ["JSON_BuildTests"] = "OFF",
            },
            Description = "JSON for Modern C++",
        });
        RegisterAlias("json", "nlohmann_json");

        Register(new PackageEntry
        {
            Name = "entt",
            GitRepository = "https://github.com/skypjack/entt.git",
            GitTag = "v3.14.0",
            Strategy = "fetchcontent",
            CmakeTargets = ["EnTT::EnTT"],
            Description = "Fast entity-component-system (ECS) library",
        });
    }
}
