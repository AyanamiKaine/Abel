using System.Text.Json.Serialization;

namespace Abel.Core;

/// <summary>
/// Describes a curated third-party package Abel can fetch via CMake FetchContent.
/// </summary>
public class PackageEntry
{
    /// <summary>
    /// Registry key users write in project.json, e.g. "sdl3" or "fmt".
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// HTTPS git URL used by FetchContent_Declare(GIT_REPOSITORY ...).
    /// </summary>
    [JsonPropertyName("git_repository")]
    public string GitRepository { get; set; } = "";

    /// <summary>
    /// Pinned git tag or commit hash used by FetchContent_Declare(GIT_TAG ...).
    /// </summary>
    [JsonPropertyName("git_tag")]
    public string GitTag { get; set; } = "";

    /// <summary>
    /// CMake target names to link, e.g. "SDL3::SDL3".
    /// </summary>
    [JsonPropertyName("cmake_targets")]
    public IList<string> CmakeTargets { get; set; } = new List<string>();

    /// <summary>
    /// CMake cache options set before FetchContent_MakeAvailable, e.g. SDL_SHARED=OFF.
    /// </summary>
    [JsonPropertyName("cmake_options")]
    public IDictionary<string, string> CmakeOptions { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registry dependency names to resolve transitively.
    /// </summary>
    [JsonPropertyName("dependencies")]
    public IList<string> Dependencies { get; set; } = new List<string>();

    /// <summary>
    /// Strategy Abel uses to consume this dependency.
    ///   fetchcontent  normal FetchContent_MakeAvailable
    ///   wrapper       FetchContent_Populate + Abel-generated CMake target
    ///   header_inject FetchContent_Populate + INTERFACE include target
    /// </summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "fetchcontent";

    /// <summary>
    /// For wrapper/header_inject: source files relative to fetched source root.
    /// </summary>
    [JsonPropertyName("sources")]
    public IList<string> Sources { get; set; } = new List<string>();

    /// <summary>
    /// Include directories relative to fetched source root.
    /// </summary>
    [JsonPropertyName("include_dirs")]
    public IList<string> IncludeDirs { get; set; } = new List<string>();

    /// <summary>
    /// Compile definitions applied to generated wrapper targets.
    /// </summary>
    [JsonPropertyName("compile_definitions")]
    public IList<string> CompileDefinitions { get; set; } = new List<string>();

    /// <summary>
    /// Optional variants, keyed by variant name (e.g. sdl3_renderer).
    /// </summary>
    [JsonPropertyName("variants")]
    public IDictionary<string, PackageVariant> Variants { get; set; } = new Dictionary<string, PackageVariant>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional human-readable summary for list/search UIs.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

/// <summary>
/// Optional package variant (backend/profile) extending a base package.
/// </summary>
public class PackageVariant
{
    [JsonPropertyName("sources")]
    public IList<string> Sources { get; set; } = new List<string>();

    [JsonPropertyName("include_dirs")]
    public IList<string> IncludeDirs { get; set; } = new List<string>();

    [JsonPropertyName("dependencies")]
    public IList<string> Dependencies { get; set; } = new List<string>();

    [JsonPropertyName("compile_definitions")]
    public IList<string> CompileDefinitions { get; set; } = new List<string>();
}
