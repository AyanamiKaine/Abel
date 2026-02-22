using Abel.Core;

namespace Abel;

internal static class RegistryCli
{
    public static int ListPackages(bool verbose)
    {
        var entries = GetSortedEntries();
        if (entries.Count == 0)
        {
            Console.WriteLine("No registry packages available.");
            return 0;
        }

        PrintList(entries, verbose);
        Console.WriteLine();
        Console.WriteLine($"Packages: {entries.Count}");
        return entries.Count;
    }

    public static int SearchPackages(string query, bool verbose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var entries = GetSortedEntries();
        var terms = SplitTerms(query);
        var matches = entries.Where(entry => Matches(entry, terms)).ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine($"No packages matching '{query}'.");
            return 0;
        }

        PrintList(matches, verbose);
        Console.WriteLine();
        Console.WriteLine($"Matches: {matches.Count}");
        return matches.Count;
    }

    public static bool TryPrintPackageInfo(string dependencySpec, bool verbose)
    {
        DependencySpec spec;
        try
        {
            spec = DependencySpec.Parse(dependencySpec);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return false;
        }

        var registry = new PackageRegistry();
        var package = registry.Find(spec.PackageName);
        if (package is null)
        {
            Console.Error.WriteLine($"error: Unknown package '{spec.PackageName}'.");
            Console.Error.WriteLine("Run 'abel list' to see available packages.");
            return false;
        }

        return PrintPackage(package, spec.VariantName, verbose);
    }

    private static List<PackageEntry> GetSortedEntries()
    {
        var registry = new PackageRegistry();
        return registry.All
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void PrintList(IReadOnlyCollection<PackageEntry> entries, bool verbose)
    {
        var nameWidth = Math.Max("Name".Length, entries.Max(entry => entry.Name.Length));
        var versionWidth = Math.Max("Version".Length, entries.Max(entry => entry.GitTag.Length));

        if (verbose)
        {
            var strategyWidth = Math.Max("Strategy".Length, entries.Max(entry => entry.Strategy.Length));
            Console.WriteLine(
                $"{Pad("Name", nameWidth)}  {Pad("Version", versionWidth)}  {Pad("Strategy", strategyWidth)}  Description");

            foreach (var entry in entries)
            {
                Console.WriteLine(
                    $"{Pad(entry.Name, nameWidth)}  {Pad(entry.GitTag, versionWidth)}  {Pad(entry.Strategy, strategyWidth)}  {FormatDescription(entry)}");
            }

            return;
        }

        Console.WriteLine($"{Pad("Name", nameWidth)}  {Pad("Version", versionWidth)}  Description");
        foreach (var entry in entries)
            Console.WriteLine($"{Pad(entry.Name, nameWidth)}  {Pad(entry.GitTag, versionWidth)}  {FormatDescription(entry)}");
    }

    private static bool PrintPackage(PackageEntry package, string? requestedVariant, bool verbose)
    {
        Console.WriteLine($"Name:         {package.Name}");
        Console.WriteLine($"Version:      {Fallback(package.GitTag)}");
        Console.WriteLine($"Repository:   {Fallback(package.GitRepository)}");
        Console.WriteLine($"Strategy:     {Fallback(package.Strategy)}");
        Console.WriteLine($"Description:  {Fallback(package.Description)}");
        Console.WriteLine($"Targets:      {FormatList(package.CmakeTargets)}");
        Console.WriteLine($"Dependencies: {FormatList(package.Dependencies)}");
        Console.WriteLine($"CMake options:{FormatMap(package.CmakeOptions)}");

        if (package.Variants.Count == 0)
        {
            Console.WriteLine("Variants:     (none)");
            return true;
        }

        Console.WriteLine($"Variants:     {string.Join(", ", package.Variants.Keys.Order(StringComparer.OrdinalIgnoreCase))}");

        if (string.IsNullOrWhiteSpace(requestedVariant))
            return true;

        if (!package.Variants.TryGetValue(requestedVariant, out var variant))
        {
            Console.Error.WriteLine($"error: Package '{package.Name}' has no variant '{requestedVariant}'.");
            Console.Error.WriteLine(
                $"available: {string.Join(", ", package.Variants.Keys.Order(StringComparer.OrdinalIgnoreCase))}");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine($"Variant '{requestedVariant}':");
        Console.WriteLine($"  Sources:             {FormatList(variant.Sources)}");
        Console.WriteLine($"  Include directories: {FormatList(variant.IncludeDirs)}");
        Console.WriteLine($"  Dependencies:        {FormatList(variant.Dependencies)}");
        Console.WriteLine($"  Definitions:         {FormatList(variant.CompileDefinitions)}");

        if (!verbose)
            return true;

        Console.WriteLine();
        Console.WriteLine("Base package sources:");
        Console.WriteLine($"  {FormatList(package.Sources)}");
        Console.WriteLine("Base package includes:");
        Console.WriteLine($"  {FormatList(package.IncludeDirs)}");
        Console.WriteLine("Base package definitions:");
        Console.WriteLine($"  {FormatList(package.CompileDefinitions)}");
        return true;
    }

    private static bool Matches(PackageEntry entry, List<string> terms)
    {
        if (terms.Count == 0)
            return true;

        var variants = entry.Variants.Keys.Order(StringComparer.OrdinalIgnoreCase);
        var haystack = string.Join(
            ' ',
            [
                entry.Name,
                entry.Description,
                entry.GitTag,
                entry.Strategy,
                string.Join(' ', entry.CmakeTargets),
                string.Join(' ', entry.Dependencies),
                string.Join(' ', variants),
            ]);

        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> SplitTerms(string query) =>
        query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static string FormatDescription(PackageEntry entry)
    {
        var description = Fallback(entry.Description);
        if (entry.Variants.Count == 0)
            return description;

        return $"{description} [variants: {string.Join(", ", entry.Variants.Keys.Order(StringComparer.OrdinalIgnoreCase))}]";
    }

    private static string Pad(string value, int width) => value.PadRight(width, ' ');

    private static string Fallback(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string FormatList(IEnumerable<string> values)
    {
        var items = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return items.Count == 0 ? "(none)" : string.Join(", ", items);
    }

    private static string FormatMap(IEnumerable<KeyValuePair<string, string>> options)
    {
        var pairs = options
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToList();

        return pairs.Count == 0 ? " (none)" : $" {string.Join(", ", pairs)}";
    }
}
