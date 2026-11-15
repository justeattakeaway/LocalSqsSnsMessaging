// Translated from behavioral/dynamodb/check_global_table_settings_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-settings-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableSettingsUpdate
{
    private const string CheckId = "global-table-settings-update";
    private static readonly string[] KnownRegions = ["us-east-1", "us-west-2", "us-east-2"];

    /// <summary>
    /// The legacy (2017.11.29) UpdateGlobalTableSettings and DescribeGlobalTableSettings both refuse
    /// GlobalTableNotFoundException, both for a two-Region global table built the 2019.11.21 way
    /// (UpdateTable ReplicaUpdates), which the legacy API never tracks, and for a name never a table.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "UpdateTable's ReplicaUpdates are not emulated: it is refused with \"At least one of ProvisionedThroughput, BillingMode, UpdateStreamEnabled, GlobalSecondaryIndexUpdates or SSESpecification ...\"")]
    [DisplayName("dynamodb.global-table-settings-update")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var secondRegion = OtherRegion(ctx.Region);

        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        // Fallback net: if the replica-create call below never runs (or raises before its own
        // combined teardown is registered), this still reclaims the bare table. Once the replica
        // exists, Teardown below supersedes this -- it removes the replica first, then deletes
        // the table itself, so this net tolerates a table that is already gone or being deleted.
        ctx.OnCleanup(() => DeleteTableIfPresent(ddb, name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = name,
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = secondRegion } }],
        });
        ctx.OnCleanup(() => Teardown(ddb, name, secondRegion));
        await WaitTableActive(ddb, name, timeoutSeconds: 240);

        var expectedMessage = $"Global table with name: '{name}' does not exist.";
        var (describeCode, describeMessage) = await ErrorCodeAndMessage(() => ddb.DescribeGlobalTableSettingsAsync(new DescribeGlobalTableSettingsRequest { GlobalTableName = name }));
        Ensure(describeCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {describeCode}");
        Ensure(describeMessage == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(describeMessage)}");

        var (updateCode, updateMessage) = await ErrorCodeAndMessage(() => ddb.UpdateGlobalTableSettingsAsync(new UpdateGlobalTableSettingsRequest
        {
            GlobalTableName = name,
            ReplicaSettingsUpdate = [new ReplicaSettingsUpdate { RegionName = secondRegion, ReplicaTableClass = TableClass.STANDARD_INFREQUENT_ACCESS }],
        }));
        Ensure(updateCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {updateCode}");
        Ensure(updateMessage == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(updateMessage)}");

        // Same rejection for a name that was NEVER a real table either.
        var bogusName = FidelityContext.UniqueName("sim2real-behav-no-such-global-table-");
        var (bogusCode, bogusMessage) = await ErrorCodeAndMessage(() => ddb.DescribeGlobalTableSettingsAsync(new DescribeGlobalTableSettingsRequest { GlobalTableName = bogusName }));
        Ensure(bogusCode == "GlobalTableNotFoundException", bogusCode ?? "None");
        var expectedBogus = $"Global table with name: '{bogusName}' does not exist.";
        Ensure(bogusMessage == expectedBogus, $"expected message {Py.Repr(expectedBogus)}, got {Py.Repr(bogusMessage)}");
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

    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError but the call succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// These are concrete, real AWS region names, not a stand-in for a genuinely global or
    /// region-pinned resource: a global-table replica MUST live in some second real region, and
    /// there is no way to derive "some other region" from ctx alone, so a short literal list of
    /// known-good regions (excluding ctx.region) is the only way to pick one.
    /// </summary>
    private static string OtherRegion(string region) => KnownRegions.FirstOrDefault(r => r != region) ?? "us-east-1";

    /// <summary>
    /// Teardown of a global table is slow for reasons AWS owns, not this check: after UpdateTable's
    /// ReplicaUpdates "Delete" is issued, the replica takes ~30-180s to disappear from
    /// describe_table's Replicas[], and DeleteTable rejects the table for that whole window.
    /// </summary>
    private static async Task Teardown(IAmazonDynamoDB ddb, string name, string secondRegion)
    {
        var updateError = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = name,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = secondRegion } }],
        }));
        if (updateError is not null && updateError.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw updateError.Exception;
        }

        async Task<bool> ReplicaGone()
        {
            var (described, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(name));
            if (error is not null)
            {
                if (error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
                {
                    throw error.Exception;
                }

                return true;
            }

            return (described!.Table.Replicas ?? []).Count == 0;
        }

        async Task<bool> TryDelete()
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return true;
            }

            var retryable = error.Code == "ResourceInUseException"
                || (error.Code == "ValidationException" && (error.Message ?? string.Empty).Contains("acted as a source region", StringComparison.Ordinal));
            if (!retryable)
            {
                throw error.Exception;
            }

            return false;
        }

        // 240 s, within the suite's cleanup budget: the replica needs up to ~180 s to disappear
        // before DeleteTable accepts the table's delete.
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(240.0))
        {
            if (await ReplicaGone() && await TryDelete())
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        await TryDelete(); // one last honest attempt; if this also raises, it is reported truthfully
    }

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

    private static Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutSeconds = 90) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(name)).Table,
            t => t.TableStatus == TableStatus.ACTIVE
                && (t.GlobalSecondaryIndexes ?? []).All(idx => (idx.IndexStatus?.Value ?? "ACTIVE") == "ACTIVE")
                && (t.Replicas ?? []).All(r => (r.ReplicaStatus?.Value ?? "ACTIVE") == "ACTIVE"),
            timeoutSeconds: timeoutSeconds,
            intervalSeconds: 1.0,
            description: $"table {name} ACTIVE");
}
