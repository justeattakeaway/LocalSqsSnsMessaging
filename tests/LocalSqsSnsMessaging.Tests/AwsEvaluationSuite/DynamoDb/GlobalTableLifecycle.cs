// Translated from behavioral/dynamodb/check_global_table_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableLifecycle
{
    private const string CheckId = "global-table-lifecycle";

    private const string LegacyVersionMessage =
        "One or more parameter values were invalid: DynamoDB global tables version 2017.11.29 is not "
        + "supported. We recommend using DynamoDB global tables version 2019.11.21, instead of version "
        + "2017.11.29 (Legacy).";

    /// <summary>
    /// For a new table, CreateGlobalTable and an UpdateGlobalTable replica Create raise the version ValidationException,
    /// DescribeGlobalTable raises GlobalTableNotFoundException and ListGlobalTables omits it, each with exact messages;
    /// UpdateTable ReplicaUpdates then adds the second-region replica.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "the legacy (2017.11.29) global-table operations are not emulated: CreateGlobalTable answers UnknownOperationException, not the version-support ValidationException; UpdateTable's ReplicaUpdates are not emulated either")]
    [DisplayName("dynamodb.global-table-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var secondRegion = SecondRegion(ctx.Region);

        var t = FidelityContext.UniqueName("sim2real-behav-gt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfPresent(ddb, t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // 1) CreateGlobalTable: legacy control plane unconditionally rejects new
        // global-table creation on modern accounts. The message must be the version gate, not some
        // unrelated validation failure.
        var (createCode, createMessage) = await ErrorCodeAndMessage(() => ddb.CreateGlobalTableAsync(new CreateGlobalTableRequest
        {
            GlobalTableName = t,
            ReplicationGroup = [new Replica { RegionName = ctx.Region }],
        }));
        Ensure(createCode == "ValidationException", $"expected ValidationException, got {createCode} ({createMessage})");
        Ensure(createMessage == LegacyVersionMessage, $"expected message {Py.Repr(LegacyVersionMessage)}, got {Py.Repr(createMessage)}");

        // 2) DescribeGlobalTable: nothing is tracked yet either.
        var (describeCode, describeMessage) = await ErrorCodeAndMessage(() => ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = t }));
        Ensure(describeCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {describeCode}");
        var expectedNotFound = $"Global table not found: Global table with name: '{t}' does not exist.";
        Ensure(describeMessage == expectedNotFound, $"expected message {Py.Repr(expectedNotFound)}, got {Py.Repr(describeMessage)}");

        // 3) ListGlobalTables: this table name is not present.
        var listedBefore = (await ddb.ListGlobalTablesAsync(new ListGlobalTablesRequest())).GlobalTables;
        Ensure(
            !listedBefore.Any(gt => gt.GlobalTableName == t),
            $"expected {t} absent from ListGlobalTables, got {Py.Repr(listedBefore.Select(gt => gt.GlobalTableName).ToList())}");

        // 4) UpdateGlobalTable: the legacy control plane rejects a replica Create with the same
        // version gate as CreateGlobalTable.
        var (updateCode, updateMessage) = await ErrorCodeAndMessage(() => ddb.UpdateGlobalTableAsync(new UpdateGlobalTableRequest
        {
            GlobalTableName = t,
            ReplicaUpdates = [new ReplicaUpdate { Create = new CreateReplicaAction { RegionName = secondRegion } }],
        }));
        Ensure(updateCode == "ValidationException", $"expected ValidationException, got {updateCode}");
        Ensure(updateMessage == LegacyVersionMessage, $"expected message {Py.Repr(LegacyVersionMessage)}, got {Py.Repr(updateMessage)}");

        // The MODERN mechanism is what actually creates a working global table:
        // UpdateTable's ReplicaUpdates. Confirm the resulting replication state is
        // visible via DescribeTable.
        await CreateReplicaWithRetry(ddb, t, secondRegion);
        ctx.OnCleanup(() => Teardown(ddb, t, secondRegion));

        // Wait for the replica to settle to ACTIVE: a replica still CREATING refuses the
        // teardown's replica Delete with ResourceInUseException.
        var table = await PollUntil(
            async () => (await ddb.DescribeTableAsync(t)).Table,
            tbl => tbl.TableStatus == TableStatus.ACTIVE
                && (tbl.Replicas ?? []).Any(r => r.RegionName == secondRegion && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 300,
            intervalSeconds: 5.0,
            description: "replica ACTIVE via DescribeTable");
        Ensure(
            table.Replicas.Any(r => r.RegionName == secondRegion),
            $"expected replica in {secondRegion} via DescribeTable, got {Py.Repr(table.Replicas.Select(r => r.RegionName).ToList())}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 60, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    private static async Task<(string? Code, string Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call succeeded and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message ?? string.Empty);
    }

    /// <summary>
    /// A region distinct from the primary, needed only as an arbitrary replica target for the
    /// modern global-table mechanism -- not a pinned/global-service literal.
    /// </summary>
    private static string SecondRegion(string primary) => primary != "us-west-2" ? "us-west-2" : "us-east-1";

    /// <summary>
    /// UpdateTable's ReplicaUpdates "Create" issued right as the source table reaches ACTIVE can
    /// transiently fail with an AccessDeniedException wrapping an "UnrecognizedClientException"/
    /// "security token" message while DynamoDB's cross-region replication setup settles -- retry for
    /// a bounded time rather than treating that as a hard failure.
    /// </summary>
    private static async Task CreateReplicaWithRetry(IAmazonDynamoDB ddb, string tableName, string region, double timeoutSeconds = 90, double intervalSeconds = 5)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = tableName,
                ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = region } }],
            }));
            if (error is null)
            {
                return;
            }

            var transient = error.Code == "AccessDeniedException"
                && (error.Message ?? string.Empty).Contains("security token", StringComparison.OrdinalIgnoreCase);
            if (!transient || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>Fallback net for the bare table; Teardown normally deletes it first.</summary>
    private static async Task DeleteTableIfPresent(IAmazonDynamoDB ddb, string name)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
        if (error is null || error.Code == "ResourceNotFoundException")
        {
            return;
        }

        if (error.Code == "ResourceInUseException" && (error.Message ?? string.Empty).Contains("being deleted", StringComparison.Ordinal))
        {
            return;
        }

        throw error.Exception;
    }

    /// <summary>
    /// Remove the replica, wait until it is gone, then delete the table. DeleteTable refuses
    /// the table with ValidationException ("acted as a source region ...") or
    /// ResourceInUseException until the replica has actually disappeared.
    /// </summary>
    private static async Task Teardown(IAmazonDynamoDB ddb, string name, string region)
    {
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(240.0);
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = name,
                ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region } }],
            }));
            if (error is null)
            {
                break;
            }

            if (error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= deadline)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
        }

        while (true)
        {
            var (described, describeError) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(name));
            if (describeError is not null)
            {
                if (describeError.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw describeError.Exception;
            }

            var replicas = described!.Table.Replicas ?? [];
            if (replicas.Count == 0)
            {
                var deleteError = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
                if (deleteError is null)
                {
                    return;
                }

                var retryable = deleteError.Code == "ResourceInUseException"
                    || (deleteError.Code == "ValidationException" && (deleteError.Message ?? string.Empty).Contains("acted as a source region", StringComparison.Ordinal));
                if (!retryable || clock.Elapsed >= deadline)
                {
                    throw deleteError.Exception;
                }
            }
            else if (clock.Elapsed >= deadline)
            {
                throw Fail($"replica {region} of {name} still present after teardown wait: {Py.Repr(replicas.Select(r => r.RegionName).ToList())}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }
    }
}
