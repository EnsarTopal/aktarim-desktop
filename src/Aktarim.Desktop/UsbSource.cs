using System.IO;
using MediaDevices;
using System.Security.Cryptography;
using System.Text;

namespace Aktarim.Desktop;

internal sealed record UsbItem(string Name, long Length, Func<Stream> Open, Func<Stream>? OpenThumbnail = null, string? SourceKey = null, DateTime? Taken = null, DateTime? SortDate = null, string? SourceLocator = null);
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
                    string? locator = null; DateTime? taken = null, modified = null;
                    try
                    {
                        modified = item.LastWriteTime;
                        if (!string.IsNullOrWhiteSpace(device.DeviceId) && !string.IsNullOrWhiteSpace(item.FullName) && modified is { Year: > 1970 })
                            locator = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{device.DeviceId}\n{item.FullName}\n{modified.Value.Ticks}\n{item.Length}")));
                    }
                    catch (Exception) { }
                    try { if (item.DateAuthored is { Year: > 1970 } date) taken = date; } catch (Exception) { }
                    result.Add(new(item.Name, checked((long)item.Length), () => item.OpenRead(), () => item.OpenThumbnail(), null, taken, modified, locator));
                }
        var duplicates = result.Where(x => x.SourceLocator != null).GroupBy(x => x.SourceLocator).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        return result.Select(item => duplicates.Contains(item.SourceLocator) ? item with { SourceLocator = null } : item).ToArray();
    }
    public void Dispose() => device.Dispose();
}

// Enabled only by explicit test command line arguments.
internal sealed class FakeUsbSource(string root) : IUsbSource
{
    public string Name => "Test USB";
    public IReadOnlyList<UsbItem> Scan() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(UsbSources.IsMedia).OrderBy(x => x).Select(path =>
    {
        var info = new FileInfo(path);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n" + info.LastWriteTimeUtc.Ticks + "\n" + info.Length)));
        bool volatileId = File.Exists(path + ".volatile-id");
        return new UsbItem(info.Name, info.Length, () => {
            if (File.Exists(path + ".track")) { string opened = path + ".opened"; File.WriteAllText(opened, (File.Exists(opened) ? int.Parse(File.ReadAllText(opened)) + 1 : 1).ToString()); }
            if (File.Exists(path + ".unreadable")) throw new UnauthorizedAccessException("Simulated unreadable source.");
            if (File.Exists(path + ".diskfull")) throw new IOException("Simulated full disk.", unchecked((int)0x80070070));
            if (File.Exists(path + ".transient")) { File.Delete(path + ".transient"); throw new IOException("Simulated transient error."); }
            if (File.Exists(path + ".slow")) return new SlowFakeStream(path);
            return info.Name.StartsWith("disconnect-", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(root, "reconnected"))
            ? new MemoryStream(File.ReadAllBytes(path)[..(int)(info.Length / 2)]) : File.OpenRead(path); },
            () => File.OpenRead(path + ".thumb"), volatileId ? null : key, File.Exists(path + ".unknown-date") ? null : info.CreationTime, SourceLocator: volatileId ? key : null);
    }).ToArray();
    public void Dispose() { }
}

internal sealed class SlowFakeStream(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(200, cancellationToken);
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
