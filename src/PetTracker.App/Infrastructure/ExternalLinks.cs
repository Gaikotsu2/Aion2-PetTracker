using System.IO;
using System.Runtime.InteropServices;
using PetTracker.Core;

namespace PetTracker.App.Infrastructure;

/// <summary>
/// Opens exactly three kinds of target with the shell: our own data folder, https://npcap.com and a TH.GL pet page
/// (https://aion2.th.gl/db/pets/pet_NNNN, see <see cref="LinkPolicy"/>). Anything else is refused, so this can never be
/// turned into a way to start or reach another program. The app itself makes no network calls; the browser does.
/// </summary>
internal static class ExternalLinks
{
    public const string NpcapUrl = LinkPolicy.NpcapUrl;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string? operation, string file, string? parameters, string? directory, int show);

    public static string PetUrl(int petId) => LinkPolicy.PetUrl(petId);

    public static bool Open(string target, string dataFolder)
    {
        var isUrl = LinkPolicy.IsAllowedUrl(target);
        var isFolder = false;
        if (!isUrl)
        {
            try { isFolder = string.Equals(Path.GetFullPath(target), Path.GetFullPath(dataFolder), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        if (!isUrl && !isFolder)
            return false;
        if (isFolder)
            Directory.CreateDirectory(target);
        return ShellExecuteW(IntPtr.Zero, "open", target, null, null, 1).ToInt64() > 32;
    }
}
