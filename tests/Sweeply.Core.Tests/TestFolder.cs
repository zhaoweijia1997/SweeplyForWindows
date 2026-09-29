using System.Diagnostics;

namespace Sweeply.Core.Tests;

/// <summary>A throw-away folder tree under %TEMP% for one test.</summary>
internal sealed class TestFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SweeplyTests", Guid.NewGuid().ToString("N"));

    public TestFolder() => Directory.CreateDirectory(Root);

    public string Dir(params string[] parts)
    {
        string p = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, int bytes, DateTime? lastWriteUtc = null)
    {
        string p = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        if (lastWriteUtc is { } t)
        {
            // An old file is old in both senses: created and last modified back then.
            System.IO.File.SetCreationTimeUtc(p, t);
            System.IO.File.SetLastWriteTimeUtc(p, t);
        }
        return p;
    }

    public static void Age(string directory, DateTime lastWriteUtc)
    {
        Directory.SetCreationTimeUtc(directory, lastWriteUtc);
        Directory.SetLastWriteTimeUtc(directory, lastWriteUtc);
    }

    /// <summary>Creates a directory junction (no admin rights needed). Returns false if it couldn't.</summary>
    public static bool TryJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so deleting never follows them.
            foreach (var d in new DirectoryInfo(Root).EnumerateDirectories("*", SearchOption.AllDirectories)
                         .Where(d => (d.Attributes & FileAttributes.ReparsePoint) != 0).ToList())
                d.Delete();
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Stands in for the Recycle Bin: deletes for real inside the test folder and records what it was asked.</summary>
internal sealed class FakeRecycleBin : IRecycleBin
{
    public List<string> Recycled { get; } = new();

    public bool TryRecycle(string path, out string? error)
    {
        Recycled.Add(path);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else System.IO.File.Delete(path);
        error = null;
        return true;
    }
}
