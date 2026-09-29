namespace Sweeply.Core;

/// <summary>The categories Sweeply knows about, resolved against the given user folders.</summary>
public static class CategoryCatalog
{
    /// <summary>How old chat pictures, videos and received files must be before they are offered.</summary>
    public static readonly TimeSpan HalfAYear = TimeSpan.FromDays(180);

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
                NamePatterns = new[] { "*.dmp" },
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
                // Beta, Dev and Canary run as msedge too.
                Roots = BrowserCacheRoots(
                    Path.Combine(local, "Microsoft", "Edge", "User Data"),
                    Path.Combine(local, "Microsoft", "Edge Beta", "User Data"),
                    Path.Combine(local, "Microsoft", "Edge Dev", "User Data"),
                    Path.Combine(local, "Microsoft", "Edge SxS", "User Data")),
                BlockingProcesses = new[] { "msedge" },
            },
            new()
            {
                Id = "chrome-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(
                    Path.Combine(local, "Google", "Chrome", "User Data"),
                    Path.Combine(local, "Google", "Chrome Beta", "User Data"),
                    Path.Combine(local, "Google", "Chrome Dev", "User Data"),
                    Path.Combine(local, "Google", "Chrome SxS", "User Data")),
                BlockingProcesses = new[] { "chrome" },
            },
            new()
            {
                Id = "firefox-cache",
                Group = CategoryGroup.Browsers,
                Roots = FirefoxCacheRoots(Path.Combine(local, "Mozilla", "Firefox", "Profiles")),
                BlockingProcesses = new[] { "firefox" },
            },
            new()
            {
                Id = "brave-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
                BlockingProcesses = new[] { "brave" },
            },
            new()
            {
                Id = "vivaldi-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(Path.Combine(local, "Vivaldi", "User Data")),
                BlockingProcesses = new[] { "vivaldi" },
            },
            new()
            {
                // Opera and Opera GX: the whole folder is the profile, and the cache is kept under Local AppData.
                Id = "opera-cache",
                Group = CategoryGroup.Browsers,
                Roots = new[] { "Opera Stable", "Opera GX Stable" }
                    .Select(name => Path.Combine(local, "Opera Software", name))
                    .SelectMany(dir => ProfileCaches(dir).Concat(BrowserCacheRoots(dir)))
                    .ToList(),
                BlockingProcesses = new[] { "opera" },
            },
            new()
            {
                Id = "yandex-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(Path.Combine(local, "Yandex", "YandexBrowser", "User Data")),
                BlockingProcesses = new[] { "browser" }, // Yandex Browser's program is browser.exe
            },
            new()
            {
                // 360 Secure Browser, 360 Speed Browser and 360 Speed Browser X.
                Id = "360-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(
                    Path.Combine(p.Roaming, "360se6", "User Data"),
                    Path.Combine(local, "360Chrome", "Chrome", "User Data"),
                    Path.Combine(local, "360ChromeX", "Chrome", "User Data")),
                BlockingProcesses = new[] { "360se", "360chrome", "360ChromeX" },
            },
            new()
            {
                Id = "qqbrowser-cache",
                Group = CategoryGroup.Browsers,
                Roots = BrowserCacheRoots(Path.Combine(local, "Tencent", "QQBrowser", "User Data")),
                BlockingProcesses = new[] { "QQBrowser" },
            },

            // ---- Chat apps (skipped while the app runs; chat history itself is never offered) ----
            new()
            {
                Id = "wechat-cache",
                Group = CategoryGroup.Chat,
                Roots = WeChatFolders.CacheRoots(p),
                BlockingProcesses = WeChatFolders.Processes,
            },
            new()
            {
                Id = "wechat-logs",
                Group = CategoryGroup.Chat,
                Roots = WeChatFolders.LogRoots(p),
                Kind = ItemKind.Files,
                NamePatterns = new[] { "*.xlog" },
                BlockingProcesses = WeChatFolders.Processes,
            },
            new()
            {
                // By chat and month. Once gone they no longer open on this PC, so only old ones, and not preselected.
                Id = "wechat-media",
                Group = CategoryGroup.Chat,
                Roots = WeChatFolders.MediaRoots(p),
                Kind = ItemKind.Folders,
                NamePatterns = new[] { "????-??" },
                MinimumAge = HalfAYear,
                BlockingProcesses = WeChatFolders.Processes,
                SelectedByDefault = false,
            },
            new()
            {
                Id = "wechat-files",
                Group = CategoryGroup.Chat,
                Roots = WeChatFolders.ReceivedFileRoots(p),
                Kind = ItemKind.Files,
                NamePatterns = new[] { "*" },
                MinimumAge = HalfAYear,
                BlockingProcesses = WeChatFolders.Processes,
                SelectedByDefault = false,
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
                NamePatterns = new[] { "*.exe", "*.msi", "*.msix", "*.msixbundle", "*.appx" },
                SelectedByDefault = false,
            },
        };
    }

    /// <summary>The disk caches inside every Chromium profile folder (Default, Profile 1, ...) of each "User Data" folder.</summary>
    private static IReadOnlyList<string> BrowserCacheRoots(params string[] userDataFolders)
    {
        var roots = new List<string>();
        foreach (string userData in userDataFolders)
        {
            if (!Directory.Exists(userData)) continue;
            foreach (string profile in Directory.EnumerateDirectories(userData))
            {
                string name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal) && name != "Guest Profile")
                    continue;
                roots.AddRange(ProfileCaches(profile));
            }
        }
        return roots;
    }

    /// <summary>A Chromium profile's disk caches: pages and images, compiled scripts, graphics.</summary>
    private static IEnumerable<string> ProfileCaches(string profile) => new[]
    {
        Path.Combine(profile, "Cache"),
        Path.Combine(profile, "Code Cache"),
        Path.Combine(profile, "GPUCache"),
    };

    /// <summary>Firefox keeps each profile's disk cache under Local AppData, apart from the profile itself.</summary>
    private static IReadOnlyList<string> FirefoxCacheRoots(string profiles)
    {
        var roots = new List<string>();
        if (!Directory.Exists(profiles)) return roots;
        foreach (string profile in Directory.EnumerateDirectories(profiles))
        {
            roots.Add(Path.Combine(profile, "cache2"));
            roots.Add(Path.Combine(profile, "startupCache"));
        }
        return roots;
    }
}
