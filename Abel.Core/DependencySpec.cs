namespace Abel.Core;

/// <summary>
/// Dependency spec parsed from project.json entries:
///   "sdl3" -> PackageName=sdl3, VariantName=null
///   "imgui/sdl3_renderer" -> PackageName=imgui, VariantName=sdl3_renderer
/// </summary>
public readonly record struct DependencySpec(string PackageName, string? VariantName)
{
    public static DependencySpec Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        var separatorIndex = spec.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex < 0)
            return new DependencySpec(spec.Trim(), null);

        var packageName = spec[..separatorIndex].Trim();
        var variantName = spec[(separatorIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(packageName))
            throw new InvalidOperationException($"Invalid dependency '{spec}': package name is empty.");

        if (string.IsNullOrWhiteSpace(variantName))
            throw new InvalidOperationException($"Invalid dependency '{spec}': variant name is empty.");

        return new DependencySpec(packageName, variantName);
    }
}
