using System.Numerics;
using System.Security.Cryptography;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Process-wide monotonic sequence number source for FIFO messages, mirroring the
/// <c>SequenceNumber</c> system attribute real SQS assigns on send. A single shared
/// counter (rather than one per queue) keeps values comparable for messages that reach
/// the same message group through different routes (direct send, batch send, SNS
/// fan-out), which the visibility-timeout redelivery path relies on to re-insert an
/// expired message at the correct position within its group.
/// </summary>
internal static class FifoSequenceNumber
{
    private static BigInteger _sequenceNumber = CreateSeed();
    private static SpinLock _sequenceSpinLock = new(false);

    private static BigInteger CreateSeed()
    {
        var bytes = new byte[16];
#if NET
        RandomNumberGenerator.Fill(bytes);
#else
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
#endif
        var randomBigInt = new BigInteger(bytes);
        return BigInteger.Abs(randomBigInt % BigInteger.Pow(10, 20));
    }

    public static BigInteger Next()
    {
        var lockTaken = false;
        try
        {
            _sequenceSpinLock.Enter(ref lockTaken);
            return ++_sequenceNumber;
        }
        finally
        {
            if (lockTaken) _sequenceSpinLock.Exit();
        }
    }
}
