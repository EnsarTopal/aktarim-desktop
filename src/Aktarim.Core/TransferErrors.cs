using System.ComponentModel;

namespace Aktarim.Core;

public static class TransferErrors
{
    public static bool IsDiskFull(Exception exception) => HasCode(exception, 39, 112);
    public static bool IsDisconnected(Exception exception) => exception is EndOfStreamException || exception.InnerException != null && IsDisconnected(exception.InnerException) || HasCode(exception, 6, 21, 30, 31, 55, 64, 1167);

    private static bool HasCode(Exception exception, params int[] codes)
    {
        if (codes.Contains(exception.HResult & 0xffff) || exception is Win32Exception win32 && codes.Contains(win32.NativeErrorCode)) return true;
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => HasCode(inner, codes))) return true;
        return exception.InnerException != null && HasCode(exception.InnerException, codes);
    }
}
