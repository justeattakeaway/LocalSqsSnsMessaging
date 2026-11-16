using LocalSqsSnsMessaging.DynamoDbStreams.Model;
using Stream = LocalSqsSnsMessaging.DynamoDbStreams.Model.Stream;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// In-memory DynamoDB Streams: ListStreams, DescribeStream, GetShardIterator and GetRecords over
/// the streams of one region's tables.
/// </summary>
internal sealed class InternalDynamoDbStreamsClient
{
    private static readonly TimeSpan IteratorLifetime = TimeSpan.FromMinutes(15);

    private readonly InMemoryAwsBus _bus;
    private readonly AwsRegionResources _resources;

    internal InternalDynamoDbStreamsClient(InMemoryAwsBus bus, string? region = null)
    {
        _bus = bus;
        _resources = bus.Region(region);
    }

    private DynamoDbStore Store => _resources.DynamoDb;

    /// <summary>The store, under its lock, with what DynamoDB Streams no longer keeps forgotten.</summary>
    private DynamoDbStore TrimmedStore()
    {
        Store.TrimStreams(_bus.TimeProvider.GetUtcNow());
        return Store;
    }

    private DynamoStream FindStream(string? arn) =>
        arn is not null && Store.Streams.TryGetValue(arn, out var stream)
            ? stream
            : throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Stream: {arn} not found");

    public Task<ListStreamsResponse> ListStreamsAsync(ListStreamsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Limit is > 100)
        {
            throw DynamoDbErrors.InvalidParameter("Limit cannot be greater than the maximum allowed size: 100");
        }

        lock (Store.Gate)
        {
            var streams = TrimmedStore().Streams.Values
                .Where(s => request.TableName is null || string.Equals(s.TableName, request.TableName, StringComparison.Ordinal))
                .OrderBy(s => s.Arn, StringComparer.Ordinal)
                .ToList();
            if (request.ExclusiveStartStreamArn is { } start)
            {
                streams = streams.Where(s => string.CompareOrdinal(s.Arn, start) > 0).ToList();
            }

            var limit = request.Limit ?? 100;
            var page = streams.Take(limit).ToList();
            _bus.RecordOperation(AwsServiceName.DynamoDb, "ListStreams");
            return Task.FromResult(new ListStreamsResponse
            {
                Streams = page.Select(s => new Stream { StreamArn = s.Arn, TableName = s.TableName, StreamLabel = s.Label }).ToList(),
                LastEvaluatedStreamArn = streams.Count > limit ? page[^1].Arn : null,
            });
        }
    }

    public Task<DescribeStreamResponse> DescribeStreamAsync(DescribeStreamRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            TrimmedStore();
            var stream = FindStream(request.StreamArn);
            var enabling = stream.EnablingUntil is { } enablingUntil && _bus.TimeProvider.GetUtcNow() < enablingUntil;
            var shards = new List<Shard>();
            if (!enabling && (request.ExclusiveStartShardId is null || string.CompareOrdinal(stream.ShardId, request.ExclusiveStartShardId) > 0))
            {
                shards.Add(new Shard
                {
                    ShardId = stream.ShardId,
                    SequenceNumberRange = new SequenceNumberRange
                    {
                        StartingSequenceNumber = stream.StartingSequenceNumber,
                        EndingSequenceNumber = stream.EndingSequenceNumber,
                    },
                });
            }

            var keySchema = new List<KeySchemaElement> { new() { AttributeName = stream.KeySchema.Partition.Name, KeyType = "HASH" } };
            if (stream.KeySchema.Sort is not null)
            {
                keySchema.Add(new KeySchemaElement { AttributeName = stream.KeySchema.Sort.Name, KeyType = "RANGE" });
            }

            _bus.RecordOperation(AwsServiceName.DynamoDb, "DescribeStream", stream.Arn);
            return Task.FromResult(new DescribeStreamResponse
            {
                StreamDescription = new StreamDescription
                {
                    StreamArn = stream.Arn,
                    StreamLabel = stream.Label,
                    StreamStatus = !stream.Enabled ? "DISABLED" : enabling ? "ENABLING" : "ENABLED",
                    StreamViewType = stream.ViewType,
                    CreationRequestDateTime = stream.CreatedAt.UtcDateTime,
                    TableName = stream.TableName,
                    KeySchema = keySchema,
                    Shards = shards,
                },
            });
        }
    }

    public Task<GetShardIteratorResponse> GetShardIteratorAsync(GetShardIteratorRequest request, CancellationToken cancellationToken = default)
    {
        lock (Store.Gate)
        {
            // GetShardIterator words a missing stream without naming it.
            var stream = request.StreamArn is not null && TrimmedStore().Streams.TryGetValue(request.StreamArn, out var found)
                ? found
                : throw DynamoDbErrors.ResourceNotFound("Requested resource not found: Stream does not exist");
            if (!string.Equals(request.ShardId, stream.ShardId, StringComparison.Ordinal))
            {
                throw DynamoDbErrors.ResourceNotFound($"Requested resource not found: Shard does not exist");
            }

            int position;
            switch (request.ShardIteratorType)
            {
                case "TRIM_HORIZON":
                    position = stream.FirstPosition;
                    break;
                case "LATEST":
                    position = stream.EndPosition;
                    break;
                case "AT_SEQUENCE_NUMBER" or "AFTER_SEQUENCE_NUMBER":
                {
                    var sequence = request.SequenceNumber ?? throw DynamoDbErrors.InvalidParameter("SequenceNumber cannot be null");
                    var at = stream.PositionOf(sequence) ?? throw DynamoDbErrors.Validation("Invalid SequenceNumber for the shard");

                    position = request.ShardIteratorType == "AT_SEQUENCE_NUMBER" ? at : at + 1;
                    break;
                }

                default:
                    throw DynamoDbErrors.Validation($"Invalid ShardIteratorType: {request.ShardIteratorType}");
            }

            _bus.RecordOperation(AwsServiceName.DynamoDb, "GetShardIterator", stream.Arn);
            return Task.FromResult(new GetShardIteratorResponse { ShardIterator = Iterator(stream, position) });
        }
    }

    private string Iterator(DynamoStream stream, int position)
    {
        var issued = _bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var text = string.Join("|", stream.Arn, stream.ShardId, position.ToString(CultureInfo.InvariantCulture), issued.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    public Task<GetRecordsResponse> GetRecordsAsync(GetRecordsRequest request, CancellationToken cancellationToken = default)
    {
        string[] parts;
        try
        {
            parts = Encoding.UTF8.GetString(Convert.FromBase64String(request.ShardIterator ?? string.Empty)).Split('|');
        }
        catch (FormatException)
        {
            parts = [];
        }

        if (parts.Length != 4
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var issued))
        {
            throw DynamoDbErrors.Validation("Invalid ShardIterator");
        }

        if (request.Limit is { } requested && (requested < 1 || requested > 1000))
        {
            throw DynamoDbErrors.ValidationClause(AwsValidation.Clause(requested.ToString(CultureInfo.InvariantCulture), "limit", requested < 1 ? "Member must have value greater than or equal to 1" : "Member must have value less than or equal to 1000"));
        }

        lock (Store.Gate)
        {
            TrimmedStore();
            var stream = FindStream(parts[0]);
            if (_bus.TimeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(issued) > IteratorLifetime)
            {
                throw new DynamoDbException("ExpiredIteratorException", "Iterator expired. The iterator was created more than 15 minutes ago.");
            }

            if (position < stream.FirstPosition)
            {
                throw new DynamoDbException("TrimmedDataAccessException", "The operation attempted to read past the oldest stream record in a shard.");
            }

            var limit = request.Limit ?? 1000;
            var records = stream.From(position, limit);
            var next = position + records.Count;
            _bus.RecordOperation(AwsServiceName.DynamoDb, "GetRecords", stream.Arn);
            return Task.FromResult(new GetRecordsResponse
            {
                Records = records.Select(r => ToRecord(stream, r)).ToList(),
                NextShardIterator = !stream.Enabled && next >= stream.EndPosition ? null : Iterator(stream, next),
            });
        }
    }

    private static Record ToRecord(DynamoStream stream, DynamoStreamRecord record) => new()
    {
        EventID = record.EventId,
        EventName = record.EventName,
        EventVersion = "1.1",
        EventSource = "aws:dynamodb",
        AwsRegion = stream.Region,
        Dynamodb = new StreamRecord
        {
            ApproximateCreationDateTime = DateTimeOffset.FromUnixTimeSeconds(record.CreatedAt.ToUnixTimeSeconds()).UtcDateTime,
            Keys = record.Keys,
            NewImage = record.NewImage as Dictionary<string, DynamoValue>,
            OldImage = record.OldImage as Dictionary<string, DynamoValue>,
            SequenceNumber = record.SequenceNumber,
            SizeBytes = record.SizeBytes,
            StreamViewType = stream.ViewType,
        },
        UserIdentity = record.ByTimeToLive ? new Identity { PrincipalId = "dynamodb.amazonaws.com", Type = "Service" } : null,
    };
}
