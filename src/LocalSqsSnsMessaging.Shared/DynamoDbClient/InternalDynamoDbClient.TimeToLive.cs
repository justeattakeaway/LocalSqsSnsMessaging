using LocalSqsSnsMessaging.DynamoDb.Model;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>Time to live and streams: UpdateTimeToLive, DescribeTimeToLive, and starting a table's stream.</summary>
internal sealed partial class InternalDynamoDbClient
{
    public Task<UpdateTimeToLiveResponse> UpdateTimeToLiveAsync(UpdateTimeToLiveRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            Authorize(table, "UpdateTimeToLive");
            if (table.Replication is { MultiRegionConsistency: "STRONG" })
            {
                throw DynamoDbErrors.Validation("Time To Live is not supported for global tables with MultiRegionConsistency set to STRONG.");
            }

            var specification = request.TimeToLiveSpecification!;
            var enable = specification.Enabled ?? false;
            if (table.TimeToLiveEnabled && !string.Equals(table.TimeToLiveAttribute, specification.AttributeName, StringComparison.Ordinal))
            {
                throw DynamoDbErrors.Validation($"TimeToLive is active on a different AttributeName: current AttributeName is {table.TimeToLiveAttribute}");
            }

            if (enable && table.TimeToLiveEnabled)
            {
                throw DynamoDbErrors.Validation("TimeToLive is already enabled");
            }

            if (!enable && !table.TimeToLiveEnabled)
            {
                throw DynamoDbErrors.Validation("TimeToLive is already disabled");
            }

            // "Any additional UpdateTimeToLive calls for the same table during this one hour
            // duration result in a ValidationException."
            var now = _bus.TimeProvider.GetUtcNow();
            if (table.TimeToLiveModifiedAt is { } modified && now < modified + TimeSpan.FromHours(1))
            {
                throw DynamoDbErrors.Validation("Time to live has been modified multiple times within a fixed interval");
            }

            var attribute = enable ? specification.AttributeName : null;
            SetTimeToLive(Store, table, attribute, now);

            // A global table's time to live is one setting for every replica.
            foreach (var member in table.Replication?.Members ?? [])
            {
                if (!ReferenceEquals(member.Table, table))
                {
                    SetTimeToLive(member.Store, member.Table, attribute, now);
                }
            }

            Record("UpdateTimeToLive", table.Arn);
            return Task.FromResult(new UpdateTimeToLiveResponse
            {
                TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = enable, AttributeName = specification.AttributeName },
            });
        }
    }

    /// <summary>
    /// Turns a table of <paramref name="store"/>'s region on to time to live on
    /// <paramref name="attribute"/> (off when null), starting or stopping its sweep.
    /// </summary>
    private void SetTimeToLive(DynamoDbStore store, DynamoTable table, string? attribute, DateTimeOffset? modifiedAt)
    {
        table.TimeToLiveModifiedAt = modifiedAt;
        table.TimeToLiveEnabled = attribute is not null;
        table.TimeToLiveAttribute = attribute;
        if (store.TimeToLiveJobs.TryGetValue(table.Name, out var job))
        {
            job.Dispose();
            store.TimeToLiveJobs.Remove(table.Name);
        }

        if (attribute is not null)
        {
            store.TimeToLiveJobs[table.Name] = new DynamoTimeToLiveJob(_bus, store, table);
        }
    }

    public Task<DescribeTimeToLiveResponse> DescribeTimeToLiveAsync(DescribeTimeToLiveRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            var table = ExistingTable(request.TableName!);
            Authorize(table, "DescribeTimeToLive");
            Record("DescribeTimeToLive", table.Arn);
            return Task.FromResult(new DescribeTimeToLiveResponse
            {
                TimeToLiveDescription = table.TimeToLiveEnabled
                    ? new TimeToLiveDescription { TimeToLiveStatus = "ENABLED", AttributeName = table.TimeToLiveAttribute }
                    : new TimeToLiveDescription { TimeToLiveStatus = "DISABLED" },
            });
        }
    }

    /// <summary>Starts a new stream for a table whose stream was just enabled.</summary>
    private void StartStream(DynamoTable table) => StartStream(table, Store);

    /// <summary>Starts a new stream for a table of <paramref name="store"/>'s region (a replica's, say).</summary>
    private void StartStream(DynamoTable table, DynamoDbStore store)
    {
        var now = _bus.TimeProvider.GetUtcNow();
        if (table.LatestStream is { } previous && previous.CreatedAt >= now)
        {
            // Two streams can't share a label; DynamoDB's labels are at least a millisecond apart.
            now = previous.CreatedAt.AddMilliseconds(1);
        }

        // A table deleted and recreated under its name has the same ARN, and its old streams stay
        // readable (for a day), so the new stream's label must also differ from theirs.
        store.TrimStreams(now);
        while (store.Streams.ContainsKey(DynamoStream.ArnFor(table.Arn, now)))
        {
            now = now.AddMilliseconds(1);
        }

        var stream = new DynamoStream(table, table.StreamViewType!, now, store.Region);
        table.LatestStream = stream;
        store.Streams[stream.Arn] = stream;
    }
}

/// <summary>
/// Deletes a table's expired items, as DynamoDB's time to live does: an item whose TTL attribute
/// is a number of epoch seconds in the past is deleted (and the deletion streamed as a service
/// deletion). Runs on the bus's <see cref="TimeProvider"/> every
/// <see cref="InMemoryAwsBus.DynamoDbTimeToLiveSweepInterval"/>, so a test controlling time
/// controls when items expire.
/// </summary>
internal sealed class DynamoTimeToLiveJob : IDisposable
{
    private readonly ITimer _timer;
    private readonly InMemoryAwsBus _bus;
    private readonly DynamoDbStore _store;
    private readonly DynamoTable _table;

    public DynamoTimeToLiveJob(InMemoryAwsBus bus, DynamoDbStore store, DynamoTable table)
    {
        _bus = bus;
        _store = store;
        _table = table;
        var interval = bus.DynamoDbTimeToLiveSweepInterval;
        _timer = bus.TimeProvider.CreateTimer(_ => Sweep(), null, interval, interval);
    }

    public void Sweep()
    {
        lock (_store.Gate)
        {
            if (!_table.TimeToLiveEnabled || _table.TimeToLiveAttribute is not { } attribute
                || !_store.Tables.TryGetValue(_table.Name, out var current) || !ReferenceEquals(current, _table))
            {
                return;
            }

            // An expiry more than five years in the past is not considered for deletion at all.
            var utcNow = _bus.TimeProvider.GetUtcNow();
            var now = utcNow.ToUnixTimeSeconds();
            var oldest = utcNow.AddYears(-5).ToUnixTimeSeconds();
            var expired = _table.All()
                .Where(entry => entry.Item.TryGetValue(attribute, out var value)
                    && value.Type == DynamoType.N
                    && DynamoNumber.TryParse(value.N, out var seconds)
                    && seconds.ToDouble() is var expiresAt
                    && expiresAt <= now
                    && expiresAt >= oldest)
                .Select(entry => entry.Key)
                .ToList();
            foreach (var key in expired)
            {
                var change = _table.Delete(key, byTimeToLive: true);
                var at = _bus.TimeProvider.GetUtcNow();
                if (_table.StreamEnabled && _table.LatestStream is { Enabled: true } stream)
                {
                    stream.Append(change, _table, at);
                }

                _table.Replication?.Replicate(_table, change, at);
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
