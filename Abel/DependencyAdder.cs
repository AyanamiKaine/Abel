using Abel.Core;
using System.Text.Json;

namespace Abel;

internal static class DependencyAdder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static bool TryAddDependencies(IReadOnlyList<string> args, bool verbose)
    {
        try
        {
            var options = ParseOptions(args);
            var projectFilePath = ResolveProjectFilePath(options.ProjectPath);
            var config = ReadProjectConfig(projectFilePath);
            var registry = new PackageRegistry();

            var requestedSpecs = ResolveRequestedSpecs(options.DependencySpecs, registry);
            var expandedSpecs = ExpandRequestedSpecs(requestedSpecs, registry);

            var changed = ApplyDependencies(config, expandedSpecs, out var added, out var alreadyPresent);
            if (!changed)
            {
                Console.WriteLine($"No changes. All dependencies already exist in '{config.Name}'.");
                return true;
            }

            WriteProjectConfig(projectFilePath, config);
            PrintResult(config.Name, added, alreadyPresent, verbose);
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

    private static AddOptions ParseOptions(IReadOnlyList<string> args)
    {
        var dependencySpecs = new List<string>();
        var projectPath = Environment.CurrentDirectory;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token.Equals("--project", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                projectPath = ReadOptionValue(args, ref i, token);
                continue;
            }

            if (token.StartsWith('-'))
                throw new InvalidOperationException($"Unknown option '{token}' for command 'add'.");

            dependencySpecs.Add(token);
        }

        if (dependencySpecs.Count == 0)
            throw new InvalidOperationException("Command 'add' requires at least one dependency. Example: abel add sdl3");

        return new AddOptions(projectPath, dependencySpecs);
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

    private static string ResolveProjectFilePath(string inputPath)
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

    private static ProjectConfig ReadProjectConfig(string projectFilePath)
    {
        var json = File.ReadAllText(projectFilePath);
        var config = JsonSerializer.Deserialize<ProjectConfig>(json);
        return config ?? throw new InvalidOperationException($"Could not parse '{projectFilePath}'.");
    }

    private static void WriteProjectConfig(string projectFilePath, ProjectConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(projectFilePath, json + "\n");
    }

    private static List<string> ResolveRequestedSpecs(List<string> inputSpecs, PackageRegistry registry)
    {
        var resolvedSpecs = new List<string>(inputSpecs.Count);
        foreach (var input in inputSpecs)
            resolvedSpecs.Add(ResolveOrSuggestSpec(input, registry));
        return resolvedSpecs;
    }

    private static string ResolveOrSuggestSpec(string inputSpec, PackageRegistry registry)
    {
        var spec = TryParseSpec(inputSpec);
        if (spec is null)
            return ResolveUnknownSpec(inputSpec, BuildAllCandidates(registry));

        var package = registry.Find(spec.Value.PackageName);
        if (package is null)
            return ResolveUnknownSpec(inputSpec, BuildPackageCandidates(registry));

        if (spec.Value.VariantName is null)
            return package.Name;

        var canonicalVariant = TryGetCanonicalVariantName(package, spec.Value.VariantName);
        if (canonicalVariant is not null)
            return $"{package.Name}/{canonicalVariant}";

        var variantSuggestion = FindBestSuggestion(spec.Value.VariantName, BuildVariantCandidates(package));
        if (variantSuggestion is null)
            throw new InvalidOperationException(
                $"Package '{package.Name}' has no variant '{spec.Value.VariantName}'.");

        var suggestion = $"{package.Name}/{variantSuggestion}";
        if (AskForSuggestion($"Package '{package.Name}' has no variant '{spec.Value.VariantName}'.", suggestion))
            return suggestion;

        throw new InvalidOperationException("Dependency add aborted.");
    }

    private static DependencySpec? TryParseSpec(string inputSpec)
    {
        try
        {
            return DependencySpec.Parse(inputSpec);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string ResolveUnknownSpec(string inputSpec, List<string> candidates)
    {
        var suggestion = FindBestSuggestion(inputSpec, candidates);
        if (suggestion is null)
            throw new InvalidOperationException($"Unknown dependency '{inputSpec}'. Run 'abel list' to inspect packages.");

        if (AskForSuggestion($"Unknown dependency '{inputSpec}'.", suggestion))
            return suggestion;

        throw new InvalidOperationException("Dependency add aborted.");
    }

    private static bool AskForSuggestion(string prefixMessage, string suggestion)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException($"{prefixMessage} Did you mean '{suggestion}'?");

        Console.Write($"{prefixMessage} Did you mean '{suggestion}'? [Y/n]: ");
        var response = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(response))
            return true;

        return response.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               response.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ExpandRequestedSpecs(List<string> requestedSpecs, PackageRegistry registry)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(NameComparer);

        foreach (var specText in requestedSpecs)
            ExpandSpec(specText, registry, result, seen);

        return result;
    }

    private static void ExpandSpec(string specText, PackageRegistry registry, List<string> output, HashSet<string> seen)
    {
        var spec = DependencySpec.Parse(specText);
        var package = registry.Find(spec.PackageName);
        if (package is null)
        {
            AddUnique(output, seen, specText);
            return;
        }

        var variant = ResolveVariant(package, spec.VariantName);

        foreach (var dep in package.Dependencies)
            ExpandSpec(dep, registry, output, seen);
        foreach (var dep in variant?.Variant.Dependencies ?? [])
            ExpandSpec(dep, registry, output, seen);

        var canonical = spec.VariantName is null ? package.Name : $"{package.Name}/{variant!.Name}";
        AddUnique(output, seen, canonical);
    }

    private static PackageVariantWithName? ResolveVariant(PackageEntry package, string? variantName)
    {
        if (variantName is null)
            return null;

        foreach (var kvp in package.Variants)
        {
            if (kvp.Key.Equals(variantName, StringComparison.OrdinalIgnoreCase))
                return new PackageVariantWithName(kvp.Key, kvp.Value);
        }

        throw new InvalidOperationException(
            $"Package '{package.Name}' has no variant '{variantName}'.");
    }

    private static void AddUnique(List<string> output, HashSet<string> seen, string value)
    {
        if (!seen.Add(value))
            return;
        output.Add(value);
    }

    private static bool ApplyDependencies(
        ProjectConfig config,
        List<string> newSpecs,
        out List<string> added,
        out List<string> alreadyPresent)
    {
        var existing = new HashSet<string>(config.Dependencies, NameComparer);
        added = new List<string>();
        alreadyPresent = new List<string>();

        foreach (var spec in newSpecs)
        {
            if (existing.Add(spec))
            {
                config.Dependencies.Add(spec);
                added.Add(spec);
            }
            else
            {
                alreadyPresent.Add(spec);
            }
        }

        return added.Count > 0;
    }

    private static void PrintResult(string projectName, List<string> added, List<string> alreadyPresent, bool verbose)
    {
        Console.WriteLine($"Updated dependencies for '{projectName}':");
        foreach (var dep in added)
            Console.WriteLine($"  + {dep}");

        if (verbose && alreadyPresent.Count > 0)
        {
            Console.WriteLine("Already present:");
            foreach (var dep in alreadyPresent)
                Console.WriteLine($"  = {dep}");
        }
    }

    private static string? TryGetCanonicalVariantName(PackageEntry package, string variantName)
    {
        foreach (var key in package.Variants.Keys)
        {
            if (key.Equals(variantName, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return null;
    }

    private static List<string> BuildPackageCandidates(PackageRegistry registry) =>
        registry.All
            .Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> BuildVariantCandidates(PackageEntry package) =>
        package.Variants.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> BuildAllCandidates(PackageRegistry registry)
    {
        var candidates = new List<string>();
        foreach (var package in registry.All.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(package.Name);
            foreach (var variant in package.Variants.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                candidates.Add($"{package.Name}/{variant}");
        }

        return candidates;
    }

    private static string? FindBestSuggestion(string input, List<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = ComputeLevenshtein(input, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        if (best is null)
            return null;

        var threshold = Math.Max(2, Math.Min(6, best.Length / 3));
        return bestDistance <= threshold ? best : null;
    }

    private static int ComputeLevenshtein(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record AddOptions(string ProjectPath, List<string> DependencySpecs);
    private sealed record PackageVariantWithName(string Name, PackageVariant Variant);
}
