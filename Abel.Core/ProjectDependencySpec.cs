namespace Abel.Core;

/// <summary>
/// Dependency spec accepted in project.json dependencies:
///   "sdl3"                              -> package dependency
///   "imgui/sdl3_renderer"               -> package dependency with variant
///   "math_module@https://repo.git"      -> git dependency
///   "math_module@https://repo.git#v1.0" -> git dependency pinned to tag/branch/commit
/// </summary>
public readonly record struct ProjectDependencySpec(
    string Name,
    string? VariantName,
    string? GitRepository,
    string? GitTag)
{
    public bool IsGit => !string.IsNullOrWhiteSpace(GitRepository);

    public static ProjectDependencySpec Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        var trimmed = spec.Trim();

        var atIndex = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0)
        {
            var candidateRepository = trimmed[(atIndex + 1)..].Trim();
            if (LooksLikeGitRepository(candidateRepository))
                return ParseGitDependency(trimmed, atIndex);
        }

        var dependencySpec = DependencySpec.Parse(trimmed);
        return new ProjectDependencySpec(
            Name: dependencySpec.PackageName,
            VariantName: dependencySpec.VariantName,
            GitRepository: null,
            GitTag: null);
    }

    private static ProjectDependencySpec ParseGitDependency(string fullSpec, int atIndex)
    {
        var name = fullSpec[..atIndex].Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"Invalid dependency '{fullSpec}': module name is empty.");

        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invalid dependency '{fullSpec}': variant syntax is not supported for git dependencies.");
        }

        var repositoryAndTag = fullSpec[(atIndex + 1)..].Trim();
        var tagSeparatorIndex = repositoryAndTag.IndexOf('#', StringComparison.Ordinal);

        var repository = tagSeparatorIndex < 0
            ? repositoryAndTag
            : repositoryAndTag[..tagSeparatorIndex].Trim();

        if (string.IsNullOrWhiteSpace(repository))
            throw new InvalidOperationException($"Invalid dependency '{fullSpec}': git repository URL is empty.");

        string? gitTag = null;
        if (tagSeparatorIndex >= 0)
        {
            gitTag = repositoryAndTag[(tagSeparatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(gitTag))
            {
                throw new InvalidOperationException(
                    $"Invalid dependency '{fullSpec}': git tag after '#' cannot be empty.");
            }
        }

        return new ProjectDependencySpec(
            Name: name,
            VariantName: null,
            GitRepository: repository,
            GitTag: gitTag);
    }

    private static bool LooksLikeGitRepository(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        return candidate.Contains("://", StringComparison.Ordinal) ||
               candidate.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
    }
}
