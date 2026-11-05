using System.Security.Cryptography;

namespace Aktarim.Core;

public static class UsbResumableCopy
{
    public static async Task<string> CopyAsync(Stream source, long length, string destination, string fileName, string partialDirectory, string identity,
        CancellationToken token = default, IProgress<long>? progress = null, Action? verifying = null, Action<string>? verifiedHash = null, Action<bool>? validatingPartial = null, Func<Task>? checkpoint = null)
    {
        if (length < 0 || string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A length and source identity are required.");
        partialDirectory = Path.GetFullPath(partialDirectory);
        using var guard = SafeDirectories.Open(partialDirectory, create: true, privateFrom: partialDirectory);
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity + "\n" + Path.GetFullPath(destination) + "\n" + fileName + "\n" + length)));
        string partial = Path.Combine(partialDirectory, key + ".partial");
        string result;
        await using (var saved = SafeDirectories.OpenPartial(partial))
        {
            if (saved.Length > length) saved.SetLength(0);
            long existing = saved.Length, position = 0;
            if (existing > 0) validatingPartial?.Invoke(true);
            byte[] buffer = new byte[1024 * 1024], previous = new byte[buffer.Length];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            bool changedPartial = false;
            while (position < existing)
            {
                if (checkpoint != null) await checkpoint();
                int requested = (int)Math.Min(buffer.Length, existing - position);
                int read = await source.ReadAsync(buffer.AsMemory(0, requested), token);
                if (read == 0) throw new UsbSourceEndedException(position, length, true);
                await saved.ReadExactlyAsync(previous.AsMemory(0, read), token);
                if (!buffer.AsSpan(0, read).SequenceEqual(previous.AsSpan(0, read)))
                {
                    saved.SetLength(0);
                    changedPartial = true;
                    position += read;
                    break;
                }
                hash.AppendData(buffer, 0, read); position += read; progress?.Report(position);
            }
            if (existing > 0) validatingPartial?.Invoke(false);
            int count;
            while (true)
            {
                if (checkpoint != null) await checkpoint();
                count = await source.ReadAsync(buffer, token);
                if (count == 0) break;
                position = checked(position + count);
                if (position > length && !changedPartial) throw new InvalidDataException("Source exceeded expected length.");
                if (!changedPartial)
                {
                    hash.AppendData(buffer, 0, count);
                    await saved.WriteAsync(buffer.AsMemory(0, count), token);
                }
                progress?.Report(position);
            }
            // Finish the resource before rejecting a stale prefix; releasing a partially read iPhone stream can corrupt the next read.
            if (changedPartial) throw new IOException("Source or partial copy changed; retry from the beginning.");
            if (position != length) throw new UsbSourceEndedException(position, length);
            await saved.FlushAsync(token); saved.Flush(true);
            saved.Position = 0;
            byte[] sourceHash = hash.GetHashAndReset();
            byte[] savedHash = await SHA256.HashDataAsync(saved, token);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, savedHash))
            { saved.SetLength(0); throw new InvalidDataException("Partial copy hash mismatch."); }
            saved.Position = 0;
            verifying?.Invoke();
            result = await UsbCopy.CopyAsync(saved, length, destination, fileName, token, verifying: verifying, verifiedHash: verifiedHash);
        }
        try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return result;
    }
}
