using System.IO;
using MediaDevices;

namespace Aktarim.Desktop;

internal sealed record UsbItem(string Name, long Length, Func<Stream> Open, Func<Stream>? OpenThumbnail = null);
internal interface IUsbSource : IDisposable
{
    string Name { get; }
    IReadOnlyList<UsbItem> Scan();
}

internal static class UsbSources
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".tif", ".tiff", ".mov", ".mp4", ".m4v", ".avi" };
    public static bool IsMedia(string name) => Extensions.Contains(Path.GetExtension(name));
    public static IReadOnlyList<IUsbSource> Find() => (MediaDeviceManager.Instance.GetDevices() ?? []).Select(d => (IUsbSource)new WpdSource(d)).ToArray();
}

internal sealed class WpdSource(MediaDevice device) : IUsbSource
{
    public string Name => device.FriendlyName ?? "WPD";
    public IReadOnlyList<UsbItem> Scan()
    {
        if (!device.IsConnected) device.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Read, false);
        var result = new List<UsbItem>();
        foreach (var drive in device.GetDrives() ?? [])
            foreach (var file in drive.RootDirectory?.EnumerateFiles("*", SearchOption.AllDirectories) ?? [])
                if (UsbSources.IsMedia(file.Name))
                {
                    var item = file;
                    result.Add(new(item.Name, checked((long)item.Length), () => item.OpenRead(), () => item.OpenThumbnail()));
                }
        return result;
    }
    public void Dispose() => device.Dispose();
}

// Enabled only by explicit test command line arguments.
internal sealed class FakeUsbSource(string root) : IUsbSource
{
    public string Name => "Test USB";
    public IReadOnlyList<UsbItem> Scan() => Directory.EnumerateFiles(root).Where(UsbSources.IsMedia).OrderBy(x => x).Select(path =>
    {
        var info = new FileInfo(path);
        return new UsbItem(info.Name, info.Length, () => {
            if (File.Exists(path + ".track")) File.WriteAllText(path + ".opened", "");
            return info.Name.StartsWith("disconnect-", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(root, "reconnected"))
            ? new MemoryStream(File.ReadAllBytes(path)[..(int)(info.Length / 2)]) : File.OpenRead(path); },
            () => File.OpenRead(path + ".thumb"));
    }).ToArray();
    public void Dispose() { }
}
