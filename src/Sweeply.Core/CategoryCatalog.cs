namespace Sweeply.Core;

/// <summary>The categories Sweeply knows about, resolved against the given user folders.</summary>
public static class CategoryCatalog
{
    public static IReadOnlyList<CleanupCategory> Create(KnownPaths p)
    {
        string local = p.LocalAppData;
        return new List<CleanupCategory>
        {
            // ---- System ----
            new()
            {
                Id = "temp-files",
                Group = CategoryGroup.System,
                Roots = new[] { p.Temp },
                Kind = ItemKind.Children,
                MinimumAge = TimeSpan.FromDays(1),
            },
            new()
            {
                Id = "crash-dumps",
                Group = CategoryGroup.System,
                Roots = new[] { Path.Combine(local, "CrashDumps") },
                Kind = ItemKind.Files,
                FilePatterns = new[] { "*.dmp" },
            },
            new()
            {
                Id = "error-reports",
                Group = CategoryGroup.System,
                Roots = new[]
                {
                    Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"),
                },
                Kind = ItemKind.Folders,
            },

            // ---- Browsers (skipped while the browser runs) ----
            new()
            {
                Id = "edge-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(Path.Combine(local, "Microsoft", "Edge", "User Data")),
                BlockingProcesses = new[] { "msedge" },
            },
            new()
            {
                Id = "chrome-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(Path.Combine(local, "Google", "Chrome", "User Data")),
                BlockingProcesses = new[] { "chrome" },
            },

            // ---- Developer tools ----
            new()
            {
                Id = "npm-cache",
                Group = CategoryGroup.Developer,
                Roots = new[] { Path.Combine(local, "npm-cache") },
            },
            new()
            {
                Id = "pip-cache",
                Group = CategoryGroup.Developer,
                Roots = new[] { Path.Combine(local, "pip", "Cache") },
            },
            new()
            {
                Id = "nuget-cache",
                Group = CategoryGroup.Developer,
                Roots = new[]
                {
                    Path.Combine(local, "NuGet", "v3-cache"),
                    Path.Combine(local, "NuGet", "plugins-cache"),
                },
            },
            new()
            {
                Id = "yarn-cache",
                Group = CategoryGroup.Developer,
                Roots = new[] { Path.Combine(local, "Yarn", "Cache") },
            },
            new()
            {
                Id = "gradle-cache",
                Group = CategoryGroup.Developer,
                Roots = new[] { Path.Combine(p.UserProfile, ".gradle", "caches") },
                BlockingProcesses = new[] { "studio64", "idea64" },
            },

            // ---- Downloads (never selected by default) ----
            new()
            {
                Id = "download-installers",
                Group = CategoryGroup.Downloads,
                Roots = new[] { p.Downloads },
                Kind = ItemKind.Files,
                FilePatterns = new[] { "*.exe", "*.msi", "*.msix", "*.msixbundle", "*.appx" },
                SelectedByDefault = false,
            },
        };
    }

    /// <summary>The disk caches inside every Chromium profile folder (Default, Profile 1, ...).</summary>
    private static IReadOnlyList<string> BrowserCacheRoots(string userData)
    {
        var roots = new List<string>();
        if (!Directory.Exists(userData)) return roots;
        foreach (string profile in Directory.EnumerateDirectories(userData))
        {
            string name = Path.GetFileName(profile);
            if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal) && name != "Guest Profile")
                continue;
            roots.Add(Path.Combine(profile, "Cache"));
            roots.Add(Path.Combine(profile, "Code Cache"));
            roots.Add(Path.Combine(profile, "GPUCache"));
        }
        return roots;
    }
}
