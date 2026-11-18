namespace InMemoryAws.DynamoDb;

/// <summary>
/// One region's DynamoDB: its tables, their streams and backups, and the transaction tokens it
/// remembers. Everything is behind one lock (<see cref="Gate"/>), shared by every region of the cloud,
/// because a transaction spans tables and a global table's write spans regions, and each must see
/// and change them all at once.
/// </summary>
/// <param name="region">The region's name.</param>
/// <param name="gate">The cloud's DynamoDB lock.</param>
internal sealed class DynamoDbStore(string region, object gate)
{
    /// <summary>The region's name.</summary>
    public string Region { get; } = region;

    /// <summary>The lock every DynamoDB request holds while it reads or writes: the cloud's, shared by its regions.</summary>
    public object Gate { get; } = gate;

    /// <summary>The tables, in creation order (ListTables sorts by name).</summary>
    public Dictionary<string, DynamoTable> Tables { get; } = new(StringComparer.Ordinal);

    /// <summary>Every stream a table has had in the last 24 hours, including disabled ones and those of deleted tables, by ARN.</summary>
    public Dictionary<string, DynamoStream> Streams { get; } = new(StringComparer.Ordinal);

    /// <summary>Forgets the streams disabled more than 24 hours ago, and every stream's records older than that.</summary>
    public void TrimStreams(DateTimeOffset now)
    {
        foreach (var stream in Streams.Values.ToList())
        {
            if (stream.IsExpired(now))
            {
                Streams.Remove(stream.Arn);
            }
            else
            {
                stream.Trim(now);
            }
        }
    }

    /// <summary>On-demand backups, by ARN.</summary>
    public Dictionary<string, DynamoBackup> Backups { get; } = new(StringComparer.Ordinal);

    /// <summary>The <c>ClientRequestToken</c>s of recent TransactWriteItems calls, for ten minutes of idempotency.</summary>
    public Dictionary<string, DynamoTransactionToken> TransactionTokens { get; } = new(StringComparer.Ordinal);

    /// <summary>Running time-to-live sweeps, by table name.</summary>
    public Dictionary<string, DynamoTimeToLiveJob> TimeToLiveJobs { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A transaction remembered by its <c>ClientRequestToken</c>, with what it consumed per table
/// (for an ExecuteTransaction replay, which is billed as reading it).
/// </summary>
internal sealed record DynamoTransactionToken(string RequestFingerprint, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, double>? Ledger = null);

/// <summary>An on-demand backup: a snapshot of a table's settings and items.</summary>
internal sealed class DynamoBackup
{
    public required string Arn { get; init; }

    public required string Name { get; init; }

    public required DateTime CreationDateTime { get; init; }

    public required DynamoTable Snapshot { get; init; }

    public required string TableName { get; init; }

    public required string TableArn { get; init; }

    public required string TableId { get; init; }

    public string Status { get; set; } = "AVAILABLE";

    public long SizeBytes { get; init; }
}
