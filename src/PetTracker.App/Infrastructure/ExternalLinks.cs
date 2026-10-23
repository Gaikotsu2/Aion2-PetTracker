using System.IO;
using System.Runtime.InteropServices;
using PetTracker.Core;

namespace PetTracker.App.Infrastructure;

/// <summary>
/// Opens exactly seven kinds of target with the shell: our own data folder, our own logs folder (inside it), our own opcodes.json, https://npcap.com,
/// https://petsouls.dev/, this project's GitHub repository and a TH.GL pet page (https://aion2.th.gl/db/pets/pet_NNNN, see <see cref="LinkPolicy"/>). Anything else is refused, so this can never be
/// turned into a way to start or reach another program. The app itself makes no network calls; the browser does.
/// </summary>
internal static class ExternalLinks
{
    public const string NpcapUrl = LinkPolicy.NpcapUrl;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string? operation, string file, string? parameters, string? directory, int show);

    public static string PetUrl(int petId) => LinkPolicy.PetUrl(petId);

    public static bool Open(string target, string dataFolder, string? opcodesFile = null, string? logsFolder = null)
    {
        var isUrl = LinkPolicy.IsAllowedUrl(target);
        var isFolder = false;
        var isOpcodes = false;
        if (!isUrl)
        {
            try
            {
                var full = Path.GetFullPath(target);
                isFolder = string.Equals(full, Path.GetFullPath(dataFolder), StringComparison.OrdinalIgnoreCase)
                    || (logsFolder is not null && string.Equals(full, Path.GetFullPath(logsFolder), StringComparison.OrdinalIgnoreCase));
                isOpcodes = opcodesFile is not null && string.Equals(full, Path.GetFullPath(opcodesFile), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        if (!isUrl && !isFolder && !isOpcodes)
            return false;
        if (isFolder)
            Directory.CreateDirectory(target);
        if (isOpcodes && !File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            File.WriteAllText(target, "{}" + Environment.NewLine);
        }
        return ShellExecuteW(IntPtr.Zero, "open", target, null, null, 1).ToInt64() > 32;
    }
}
