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
            new()
            {
                // DirectX and the graphics drivers compile shaders once and keep them here. Rebuilt the next
                // time a game or app needs them (that first start may take a little longer).
                Id = "gpu-shader-cache",
                Group = CategoryGroup.System,
                Roots = new[]
                {
                    Path.Combine(local, "D3DSCache"),
                    Path.Combine(local, "NVIDIA", "DXCache"),
                    Path.Combine(local, "NVIDIA", "GLCache"),
                    Path.Combine(local, "NVIDIA Corporation", "NV_Cache"),
                    Path.Combine(local, "AMD", "DxCache"),
                    Path.Combine(local, "AMD", "DxcCache"),
                    Path.Combine(local, "AMD", "GLCache"),
                    Path.Combine(local, "AMD", "VkCache"),
                    Path.Combine(p.UserProfile, "AppData", "LocalLow", "Intel", "ShaderCache"),
                    Path.Combine(local, "Intel", "ShaderCache"),
                },
                MinimumAge = TimeSpan.FromDays(1), // a running game may be writing to its newest ones
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
            new()
            {
                Id = "wecom-cache",
                Group = CategoryGroup.Chat,
                Roots = WeComFolders.CacheRoots(p),
                BlockingProcesses = WeComFolders.Processes,
            },
            new()
            {
                Id = "wecom-logs",
                Group = CategoryGroup.Chat,
                Roots = WeComFolders.LogRoots(p),
                Kind = ItemKind.Files,
                NamePatterns = new[] { "*.log", "*.xlog" },
                BlockingProcesses = WeComFolders.Processes,
            },
            new()
            {
                Id = "wecom-media",
                Group = CategoryGroup.Chat,
                Roots = WeComFolders.MediaRoots(p),
                Kind = ItemKind.Folders,
                NamePatterns = new[] { "????-??" },
                MinimumAge = HalfAYear,
                BlockingProcesses = WeComFolders.Processes,
                SelectedByDefault = false,
            },
            new()
            {
                Id = "wecom-files",
                Group = CategoryGroup.Chat,
                Roots = WeComFolders.ReceivedFileRoots(p),
                Kind = ItemKind.Files,
                NamePatterns = new[] { "*" },
                MinimumAge = HalfAYear,
                BlockingProcesses = WeComFolders.Processes,
                SelectedByDefault = false,
            },
            // Apps built on Chromium: only folders that look like a Chromium cache inside (see ChromiumCaches).
            WebCache("qq-cache", CategoryGroup.Chat, new[] { "QQ" },
                Path.Combine(p.Roaming, "QQ")),
            WebCache("dingtalk-cache", CategoryGroup.Chat, new[] { "DingTalk" },
                Path.Combine(p.Roaming, "DingTalk"), Path.Combine(local, "DingTalk")),
            WebCache("feishu-cache", CategoryGroup.Chat, new[] { "Feishu", "Lark" },
                Path.Combine(p.Roaming, "LarkShell"), Path.Combine(local, "LarkShell"),
                Path.Combine(p.Roaming, "Feishu"), Path.Combine(local, "Feishu")),

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
            new()
            {
                // VS Code and the editors built from it: web caches, compiled-code caches of past versions,
                // downloaded extension packages, crash dumps and dated log folders. Settings ("User") and
                // what extensions store ("WebStorage") are never offered.
                Id = "vscode-cache",
                Group = CategoryGroup.Developer,
                Roots = new[] { "Code", "Code - Insiders", "VSCodium", "Cursor" }
                    .Select(name => Path.Combine(p.Roaming, name))
                    .SelectMany(dir => ChromiumCaches.Find(dir, 1).Concat(new[]
                    {
                        Path.Combine(dir, "CachedData"),
                        Path.Combine(dir, "CachedExtensionVSIXs"),
                        Path.Combine(dir, "Crashpad", "reports"),
                        Path.Combine(dir, "logs"),
                    }))
                    .ToList(),
                BlockingProcesses = new[] { "Code", "Code - Insiders", "VSCodium", "Cursor" },
            },
            new()
            {
                Id = "go-cache",
                Group = CategoryGroup.Developer,
                Roots = new[] { Path.Combine(local, "go-build") },
            },
            new()
            {
                // Downloaded crates and their unpacked sources; cargo fetches them again when a build needs them.
                Id = "cargo-cache",
                Group = CategoryGroup.Developer,
                Roots = new[]
                {
                    Path.Combine(p.UserProfile, ".cargo", "registry", "cache"),
                    Path.Combine(p.UserProfile, ".cargo", "registry", "src"),
                },
                BlockingProcesses = new[] { "cargo" },
            },

            // ---- Other apps: built-in web caches only ----
            WebCache("steam-cache", CategoryGroup.Apps, new[] { "steam", "steamwebhelper" },
                Path.Combine(local, "Steam")),
            WebCache("discord-cache", CategoryGroup.Apps, new[] { "Discord" },
                Path.Combine(p.Roaming, "discord")),
            WebCache("slack-cache", CategoryGroup.Apps, new[] { "slack" },
                Path.Combine(p.Roaming, "Slack")),
            WebCache("spotify-cache", CategoryGroup.Apps, new[] { "Spotify" },
                Path.Combine(p.Roaming, "Spotify"), Path.Combine(local, "Spotify")),
            WebCache("teams-cache", CategoryGroup.Apps, new[] { "Teams" },
                Path.Combine(p.Roaming, "Microsoft", "Teams")),
            WebCache("cloudmusic-cache", CategoryGroup.Apps, new[] { "cloudmusic" },
                Path.Combine(local, "NetEase", "CloudMusic")),

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

    /// <summary>
    /// An app built on Chromium: the Chromium caches found under its folders (see <see cref="ChromiumCaches"/>),
    /// skipped while the app runs. Its own data next to them is never offered.
    /// </summary>
    private static CleanupCategory WebCache(string id, CategoryGroup group, string[] processes, params string[] appFolders) => new()
    {
        Id = id,
        Group = group,
        Roots = ChromiumCaches.FindAll(appFolders),
        BlockingProcesses = processes,
    };

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
