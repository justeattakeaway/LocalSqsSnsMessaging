namespace InMemoryAws;

/// <summary>
/// The receipt handles a queue hands out with each received message. A handle names the queue (by
/// ARN and <see cref="SqsQueueResource.Incarnation"/>), the message and when it was received, so a
/// queue can tell its own handles from another queue's, or from a deleted predecessor's.
/// </summary>
internal static class SqsReceiptHandle
{
    public static string Create(SqsQueueResource queue, string messageId, DateTimeOffset receivedAt)
    {
#pragma warning disable CA1308
        var guid = Guid.NewGuid().ToString().ToLowerInvariant();
#pragma warning restore CA1308
        // Invariant: TryParse reads the timestamp back with the invariant culture.
        var decoded = FormattableString.Invariant(
            $"{guid} {queue.Arn} {messageId} {receivedAt.ToUnixTimeMilliseconds() / 1000.0} {queue.Incarnation}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded));
    }

    /// <summary>Reads the queue ARN, MessageId, receive time and queue incarnation out of a handle made by <see cref="Create"/>.</summary>
    public static bool TryParse(string receiptHandle, out string queueArn, out string messageId, out DateTimeOffset receivedAt, out string incarnation)
    {
        queueArn = string.Empty;
        messageId = string.Empty;
        receivedAt = default;
        incarnation = string.Empty;
#if NETSTANDARD2_0
        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(receiptHandle);
        }
        catch (FormatException)
        {
            return false;
        }
        var decoded = Encoding.UTF8.GetString(buffer);
#else
        var bufferLength = receiptHandle.Length * 3 / 4;
        var buffer = bufferLength <= 1024 ? stackalloc byte[bufferLength] : new byte[bufferLength];
        if (!Convert.TryFromBase64String(receiptHandle, buffer, out var written))
        {
            return false;
        }
        var decoded = Encoding.UTF8.GetString(buffer[..written]);
#endif
        if (decoded.Split(' ') is not [_, var arn, var id, var receivedSeconds, var queueIncarnation]
            || !double.TryParse(receivedSeconds, NumberStyles.Float, NumberFormatInfo.InvariantInfo, out var seconds))
        {
            return false;
        }

        queueArn = arn;
        messageId = id;
        incarnation = queueIncarnation;
        receivedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));
        return true;
    }
}

/// <summary>What became of a delete or visibility change made with a receipt handle, as SQS answers it.</summary>
internal enum SqsReceiptHandleOutcome
{
    /// <summary>Done, or (for a delete) nothing left to do: the message had already left the queue.</summary>
    Done,

    /// <summary>The message has been received again since, under a newer handle (refused as expired).</summary>
    Expired,

    /// <summary>A visibility change for a message that has left the queue (deleted, expired, moved).</summary>
    MessageNotAvailable,

    /// <summary>A visibility change that would keep the message in flight past 12 hours from its receive.</summary>
    VisibilityCapExceeded,

    /// <summary>A well-formed handle another queue handed out.</summary>
    NotThisQueue,

    /// <summary>A handle from an earlier queue of the same name, since deleted.</summary>
    QueueDeleted,

    /// <summary>Not a receipt handle.</summary>
    Invalid,
}
