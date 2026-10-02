using Microsoft.Win32;

namespace Sweeply.Core.Monitoring;

/// <summary>What this PC is made of, as Windows records it; read without administrator rights.</summary>
public static class SystemInfo
{
    /// <summary>The processor's marketing name, e.g. "Intel(R) Core(TM) Ultra 7 155H"; empty when unknown.</summary>
    public static string ProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "";
        }
    }

    public static int LogicalProcessors => Environment.ProcessorCount;
}
