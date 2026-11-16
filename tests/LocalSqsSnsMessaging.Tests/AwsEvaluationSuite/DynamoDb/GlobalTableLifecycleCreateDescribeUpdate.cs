// Translated from behavioral/dynamodb/check_global_table_lifecycle_create_describe_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-lifecycle-create-describe-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableLifecycleCreateDescribeUpdate
{
    private const string CheckId = "global-table-lifecycle-create-describe-update";

    /// <summary>
    /// A replica added through UpdateTable ReplicaUpdates shows on DescribeTable as one ACTIVE
    /// replica in its region with GlobalTableVersion 2019.11.21; removing, re-adding and removing it
    /// again is reflected on each later DescribeTable.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.global-table-lifecycle-create-describe-update")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var region1 = ctx.Region;
        var region2 = SecondaryRegion(region1);
        var globalName = FidelityContext.UniqueName("sim2real-behav-gt-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = globalName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, globalName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(globalName)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            timeoutSeconds: 60,
            intervalSeconds: 2);

        // CreateGlobalTable is refused on accounts that use global tables version 2019.11.21 ("DynamoDB
        // global tables version 2019.11.21 must be used to create new global tables"), even for a
        // single-region replication group. UpdateTable's ReplicaUpdates, issued from the table's own
        // region, makes the table global; DynamoDB creates the replica table in the target region.
        //
        // A ReplicaUpdates "Create" issued as soon as the source table (with a newly enabled stream)
        // reaches ACTIVE can be refused with an AccessDeniedException carrying a "security token"
        // message while cross-region replication settles; the create retries that refusal.
        await CreateReplicaWithRetry(ddb, globalName, region2);
        ctx.OnCleanup(() => SafeRemoveReplica(ddb, globalName, region2));

        // DescribeGlobalTable/UpdateGlobalTable serve only version 2017.11.29 tables and answer
        // GlobalTableNotFoundException for a table made global through ReplicaUpdates, so replica
        // state is read through DescribeTable (Table.Replicas[].ReplicaStatus) and changed through
        // UpdateTable.
        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(globalName)).Table,
            table => table.TableStatus == TableStatus.ACTIVE
                && (table.Replicas ?? []).Any(r => r.RegionName == region2 && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 280,
            intervalSeconds: 5);
        var regionsPresent = (described.Replicas ?? []).Select(r => r.RegionName).Append(region1).ToHashSet(StringComparer.Ordinal);
        Ensure(
            regionsPresent.SetEquals([region1, region2]),
            $"expected replica set {{{Py.Repr(region1)}, {Py.Repr(region2)}}} after ReplicaUpdates Create, got {Py.Repr(regionsPresent.ToList())}");
        AssertOneActiveReplica(described, region2);

        // With the table global across two regions, UpdateTable's ReplicaUpdates removes the replica...
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = globalName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region2 } }],
        });
        await PollUntil(
            async () => ((await ddb.DescribeTableAsync(globalName)).Table.Replicas ?? []).Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal),
            regions => !regions.Contains(region2),
            timeoutSeconds: 280,
            intervalSeconds: 5);

        // ...and adds it back. After the region leaves Replicas[], DynamoDB can still be removing the
        // replica's table in region2, and a "Create" for that region is refused with a
        // ValidationException ("Failed to create the new replica ...") until it finishes; the create
        // retries that refusal.
        await CreateReplicaWithRetry(ddb, globalName, region2);
        var updated = await PollUntil(
            async () => (await ddb.DescribeTableAsync(globalName)).Table,
            table => table.TableStatus == TableStatus.ACTIVE
                && (table.Replicas ?? []).Any(r => r.RegionName == region2 && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 280,
            intervalSeconds: 5);
        var updatedRegions = (updated.Replicas ?? []).Select(r => r.RegionName).Append(region1).ToHashSet(StringComparer.Ordinal);
        Ensure(
            updatedRegions.SetEquals([region1, region2]),
            $"expected replica set {{{Py.Repr(region1)}, {Py.Repr(region2)}}} after remove-then-re-add of {Py.Repr(region2)}, got {Py.Repr(updatedRegions.ToList())}");
        AssertOneActiveReplica(updated, region2);

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = globalName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region2 } }],
        });
        var final = await PollUntil(
            async () => (await ddb.DescribeTableAsync(globalName)).Table,
            table => (table.Replicas ?? []).Count == 0 && table.TableStatus == TableStatus.ACTIVE,
            timeoutSeconds: 280,
            intervalSeconds: 5);
        Ensure(
            (final.Replicas ?? []).Count == 0,
            $"DescribeTable Replicas after the second ReplicaUpdates Delete read {Py.Repr(final.Replicas?.Select(r => r.RegionName).ToList())}");
    }

    /// <summary>The table's own region is not listed: Replicas holds exactly the added region, ACTIVE.</summary>
    private static void AssertOneActiveReplica(TableDescription table, string region)
    {
        var replicas = table.Replicas.Select(r => (r.RegionName, r.ReplicaStatus?.Value)).ToList();
        Ensure(replicas.Count == 1 && replicas[0] == (region, "ACTIVE"), $"DescribeTable Replicas read {Py.Repr(replicas)}");
        Ensure(
            table.GlobalTableVersion == "2019.11.21",
            $"DescribeTable GlobalTableVersion is {Py.Repr(table.GlobalTableVersion)}, not '2019.11.21'");
    }

    /// <summary>A region distinct from the primary, for a global table replica.</summary>
    private static string SecondaryRegion(string primaryRegion) =>
        // Region literals: a replica lives in a second region, and ctx names only its own.
        primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";

    /// <summary>
    /// UpdateTable's ReplicaUpdates "Create" issued right as the source table reaches ACTIVE can
    /// transiently fail with an AccessDeniedException wrapping an "UnrecognizedClientException"/
    /// "security token" message while DynamoDB's cross-region replication setup settles. Re-creating
    /// a JUST-deleted replica for the same region can also transiently fail with a ValidationException
    /// ("Failed to create the new replica ...") while that region's prior replica table finishes
    /// tearing down in the background, even though it's already gone from Replicas[]. Retry for a
    /// bounded time rather than treating either as a hard failure.
    /// </summary>
    private static async Task CreateReplicaWithRetry(IAmazonDynamoDB ddb, string tableName, string region, double timeoutSeconds = 120, double intervalSeconds = 5)
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

            var text = error.Message ?? string.Empty;
            var transient = (error.Code == "AccessDeniedException" && text.Contains("security token", StringComparison.OrdinalIgnoreCase))
                || (error.Code == "ValidationException" && text.Contains("failed to create", StringComparison.OrdinalIgnoreCase));
            if (!transient || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>
    /// Remove the region's replica and wait until Replicas[] no longer lists it. A replica in
    /// CREATING/UPDATING is first polled to a deletable state; a DELETING one is only awaited.
    /// ResourceInUseException retries to the deadline; a missing table is fine; other errors raise.
    /// </summary>
    private static async Task SafeRemoveReplica(IAmazonDynamoDB ddb, string globalName, string region, double timeoutSeconds = 600, double intervalSeconds = 5)
    {
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutSeconds);

        async Task<string?> ReplicaStatusOf()
        {
            var table = (await ddb.DescribeTableAsync(globalName)).Table;
            foreach (var r in table.Replicas ?? [])
            {
                if (r.RegionName == region)
                {
                    var status = r.ReplicaStatus?.Value;
                    if (status == "DELETING")
                    {
                        return "DELETING";
                    }

                    var busy = status is "CREATING" or "UPDATING";
                    if (busy || table.TableStatus != TableStatus.ACTIVE)
                    {
                        return "BUSY";
                    }

                    return status;
                }
            }

            return null;
        }

        double Remaining() => Math.Max((deadline - clock.Elapsed).TotalSeconds, 1);

        try
        {
            var status = await PollUntil(ReplicaStatusOf, s => s != "BUSY", timeoutSeconds: Remaining(), intervalSeconds: intervalSeconds);
            if (status is null)
            {
                return;
            }

            while (status != "DELETING")
            {
                var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
                {
                    TableName = globalName,
                    ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region } }],
                }));
                if (error is null)
                {
                    break;
                }

                if (error.Code != "ResourceInUseException" || clock.Elapsed >= deadline)
                {
                    throw error.Exception;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            }

            await PollUntil(ReplicaStatusOf, s => s is null, timeoutSeconds: Remaining(), intervalSeconds: intervalSeconds);
        }
        catch (Amazon.Runtime.AmazonServiceException ex) when (ex.ErrorCode == "ResourceNotFoundException")
        {
        }
    }

    /// <summary>
    /// DeleteTable refuses with ResourceInUseException for 30-180s after a replica delete, even
    /// though the replica is already gone from Replicas[]: retry that code to a bounded deadline, then
    /// raise. A table already gone is fine; any other error raises.
    /// </summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 180, double intervalSeconds = 2)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds)
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

        throw Fail($"condition not met within {timeoutSeconds}s, last value: {Py.Repr(last)}");
    }
}
