using Aktarim.Core;
using Aktarim.Desktop;
using System.Security.Cryptography;

#pragma warning disable CA2022 // Deliberately read only part of each fake/native resource to test its lifetime.

void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
byte[] content = new byte[256 * 1024]; new Random(42).NextBytes(content);
int aborts = 0;
var normal = new Resource(content);
using (var stream = new WpdReadStream(normal, content.Length, () => { aborts++; normal.Cancelled = true; })) await stream.CopyToAsync(Stream.Null);
Check(aborts == 0 && !normal.Poisoned, "complete async read closes cleanly without cancel");
var early = new Resource(content);
using (var stream = new WpdReadStream(early, content.Length, () => aborts++)) { var buffer = new byte[20]; stream.Read(buffer, 0, 0); stream.Read(buffer, 0, 20); }
Check(aborts == 0 && !early.Poisoned, "early small-photo close drains the resource before releasing it");
var thumbnail = new Resource(content);
using (var stream = new WpdReadStream(thumbnail, null, () => aborts++)) await stream.ReadAsync(new byte[20]);
Check(aborts == 0 && !thumbnail.Poisoned, "thumbnail early close finishes only its bounded resource");
var largeThumbnail = new Resource(new byte[5 * 1024 * 1024]);
using (var stream = new WpdReadStream(largeThumbnail, null, () => { aborts++; largeThumbnail.Cancelled = true; })) stream.Read(new byte[20], 0, 20);
Check(aborts == 1 && largeThumbnail.Position <= 4 * 1024 * 1024 + 1, "oversized thumbnail is cancelled before release without reading a video");
var largeVideo = new Resource(new byte[13 * 1024 * 1024]);
using (var stream = new WpdReadStream(largeVideo, largeVideo.Length, () => { aborts++; largeVideo.Cancelled = true; })) stream.Read(new byte[20], 0, 20);
Check(aborts == 2 && largeVideo.Position == 20 && !largeVideo.Poisoned, "closing a large-video copy cancels before disposal without draining it");
var broken = new Resource(content) { BreakRead = true };
try { using var stream = new WpdReadStream(broken, content.Length, () => { aborts++; broken.Cancelled = true; }); await stream.ReadAsync(new byte[20]); throw new Exception("Read should fail"); }
catch (WpdReadException e) { Check(TransferErrors.IsDisconnected(e) && e.InnerException is IOException, "WPD read error retains cause and pauses the connection queue"); }
Check(aborts == 3 && !broken.Poisoned, "failed read cancels before native release");
var shortResource = new Resource(content[..20]);
using (var stream = new WpdReadStream(shortResource, content.Length, () => { aborts++; shortResource.Cancelled = true; })) await stream.CopyToAsync(Stream.Null);
Check(aborts == 4, "short EOF invalidates the session even after the resource ended");

string root = Path.Combine(Path.GetTempPath(), "aktarim-usb-lifetime-" + Guid.NewGuid().ToString("N"));
try
{
    var target = Path.Combine(root, "target"); var partial = Path.Combine(root, "partial");
    try { await UsbResumableCopy.CopyAsync(new MemoryStream(content[..100]), content.Length, target, "photo.jpg", partial, "same-source"); }
    catch (UsbSourceEndedException) { }
    byte[] changed = content.ToArray(); changed[50] ^= 1;
    var mismatch = new Resource(changed);
    using (var stream = new WpdReadStream(mismatch, changed.Length, () => mismatch.Cancelled = true))
    {
        try { await UsbResumableCopy.CopyAsync(stream, content.Length, target, "photo.jpg", partial, "same-source"); throw new Exception("Changed prefix accepted"); }
        catch (IOException) { Check(mismatch.Position == mismatch.Length && !File.Exists(Path.Combine(target, "photo.jpg")), "changed saved prefix reaches EOF before rejection and is never published"); }
    }
    Check(!mismatch.Poisoned, "changed partial does not poison the next WPD file");
    using var clean = new WpdReadStream(new Resource(content), content.Length, () => throw new Exception("Unexpected abort"));
    var published = await UsbResumableCopy.CopyAsync(clean, content.Length, target, "photo.jpg", partial, "same-source");
    Check(SHA256.HashData(File.ReadAllBytes(published)).SequenceEqual(SHA256.HashData(content)), "retry publishes the complete SHA256-verified source after stale partial rejection");
    byte[] multi = new byte[3 * 1024 * 1024]; new Random(73).NextBytes(multi);
    try { await UsbResumableCopy.CopyAsync(new MemoryStream(multi[..(2 * 1024 * 1024)]), multi.Length, target, "video.mov", partial, "late-mismatch"); }
    catch (UsbSourceEndedException) { }
    multi[1024 * 1024 + 50] ^= 1;
    using var late = new MemoryStream(multi);
    try { await UsbResumableCopy.CopyAsync(late, multi.Length, target, "video.mov", partial, "late-mismatch"); throw new Exception("Later mismatch accepted"); }
    catch (IOException) { Check(late.Position == multi.Length && !File.Exists(Path.Combine(target, "video.mov")), "mismatch after a verified chunk also drains without publishing"); }
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

if (args.Contains("--iphone-read-only"))
{
    using var source = UsbSources.Find().First(x => x.Name.Contains("iPhone", StringComparison.OrdinalIgnoreCase));
    var items = source.Scan(); Console.WriteLine($"iPhone scan: {items.Count} items");
    var sample = new[] { ".JPG", ".HEIC", ".MOV" }.SelectMany(ext => items.Where(x => Path.GetExtension(x.Name).Equals(ext, StringComparison.OrdinalIgnoreCase) && x.Length > 65536 && x.Length < 250_000_000).Take(ext == ".JPG" ? 3 : 1)).ToArray();
    var hashes = new Dictionary<UsbItem, byte[]>();
    foreach (var item in sample) { using var input = item.Open(); hashes[item] = await SHA256.HashDataAsync(input); Check(input.Position == item.Length, "real iPhone baseline has exact source length: " + Path.GetExtension(item.Name)); }
    Check(sample.Length >= 3, "real iPhone baseline sampled");
    foreach (var item in sample.Where(x => x.Length <= 12 * 1024 * 1024))
    {
        using (var input = item.Open()) await input.ReadAsync(new byte[65536]);
        foreach (var next in sample) { using var input = next.Open(); Check((await SHA256.HashDataAsync(input)).SequenceEqual(hashes[next]), "real iPhone SHA256 stable after partial photo close: " + Path.GetExtension(next.Name)); }
    }
    string deviceTarget = Path.Combine(Path.GetTempPath(), "aktarim-iphone-copy-" + Guid.NewGuid().ToString("N"));
    try
    {
        int index = 0;
        foreach (var item in sample)
        {
            string name = "sample-" + index++ + Path.GetExtension(item.Name);
            string destination = Path.Combine(deviceTarget, "target"), parts = Path.Combine(deviceTarget, "partial");
            // Seed deliberately stale data without altering either the phone or an existing user backup.
            try { await UsbResumableCopy.CopyAsync(new MemoryStream(new byte[32768]), item.Length, destination, name, parts, name); }
            catch (UsbSourceEndedException) { }
            using (var input = item.Open())
            {
                try { await UsbResumableCopy.CopyAsync(input, item.Length, destination, name, parts, name); throw new Exception("Stale source fixture unexpectedly matched"); }
                catch (IOException) { Check(input.Position == item.Length && !File.Exists(Path.Combine(destination, name)), "real iPhone stale prefix rejected after complete source read: " + Path.GetExtension(name)); }
            }
            bool paused = false;
            using var copy = item.Open();
            string output = await UsbResumableCopy.CopyAsync(copy, item.Length, destination, name, parts, name, checkpoint: async () =>
            {
                if (!paused && copy.Position > 0) { paused = true; await Task.Delay(1000); }
            });
            using var published = File.OpenRead(output);
            Check(paused && (await SHA256.HashDataAsync(published)).SequenceEqual(hashes[item]), "real iPhone pause/resume retry reaches disk with baseline SHA256: " + Path.GetExtension(name));
        }
    }
    finally { if (Directory.Exists(deviceTarget)) Directory.Delete(deviceTarget, true); }
    foreach (var item in sample.Where(x => x.Length > 12 * 1024 * 1024))
    {
        using (var input = item.Open()) await input.ReadAsync(new byte[65536]);
        bool recovered = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var input = item.Open();
                byte[] actual = await SHA256.HashDataAsync(input);
                Check(actual.SequenceEqual(hashes[item]), "real iPhone large resource abort/reopen preserves source SHA256");
                recovered = true; break;
            }
            catch (IOException e) when (TransferErrors.IsDisconnected(e))
            { Console.WriteLine("WPD reopen attempt " + (attempt + 1) + ": " + e.GetType().Name); await Task.Delay(500 * (1 << attempt)); }
        }
        Check(recovered, "real iPhone large resource abort recovers within bounded retries");
    }
    Console.WriteLine("PASS real iPhone read-only lifetime regression; no phone files changed");
}

sealed class Resource(byte[] data) : MemoryStream(data)
{
    public bool Cancelled, Poisoned, BreakRead;
    public override int Read(byte[] buffer, int offset, int count) => BreakRead ? throw new IOException("Simulated WPD read failure") : base.Read(buffer, offset, count);
    protected override void Dispose(bool disposing)
    {
        if (disposing) Poisoned = Position < Length && !Cancelled;
        // Keep the in-memory fixture position available for lifetime assertions.
    }
}
