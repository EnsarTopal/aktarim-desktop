namespace Aktarim.Core;

public sealed class UsbSourceEndedException(long actual, long expected, bool validatingPartial = false)
    : EndOfStreamException($"Source ended at {actual} of {expected} bytes.")
{
    public long Actual { get; } = actual;
    public long Expected { get; } = expected;
    public bool ValidatingPartial { get; } = validatingPartial;
}
