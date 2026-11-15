namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// One region's DynamoDB: its tables, their streams and backups, and the transaction tokens it
/// remembers. Everything is behind one lock (<see cref="Gate"/>), because a transaction spans
/// tables and must see and change them all at once.
/// </summary>
internal sealed class DynamoDbStore
{
    /// <summary>The lock every DynamoDB request in the region holds while it reads or writes.</summary>
    public object Gate { get; } = new();

    /// <summary>The tables, in creation order (ListTables sorts by name).</summary>
    public Dictionary<string, DynamoTable> Tables { get; } = new(StringComparer.Ordinal);

    /// <summary>Every stream a table has had, including disabled ones and those of deleted tables, by ARN.</summary>
    public Dictionary<string, DynamoStream> Streams { get; } = new(StringComparer.Ordinal);

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
