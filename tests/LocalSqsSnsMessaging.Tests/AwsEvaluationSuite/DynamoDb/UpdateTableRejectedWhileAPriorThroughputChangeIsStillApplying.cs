// Translated from behavioral/dynamodb/check_update_table_rejected_while_a_prior_throughput_change_is_still_applying.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-rejected-while-a-prior-throughput-change-is-still-applying</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableRejectedWhileAPriorThroughputChangeIsStillApplying
{
    private const string Check = "update-table-rejected-while-a-prior-throughput-change-is-still-applying";

    /// <summary>
    /// A second UpdateTable sent right after an accepted throughput change is refused with
    /// ResourceInUseException, and the first change still settles at the capacity it asked for.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-rejected-while-a-prior-throughput-change-is-still-applying")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-updating-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
        });
        ctx.OnCleanup(() => DeleteOnceActive(ddb, table));
        await PollUntil(ddb, table, d => d.TableStatus == TableStatus.ACTIVE, timeoutS: 280, description: "ACTIVE");

        var first = (await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 3, WriteCapacityUnits = 3 },
        })).TableDescription;
        Ensure(
            first.TableStatus == TableStatus.UPDATING,
            $"the accepted UpdateTable must describe the table as UPDATING, got {Py.Repr(first.TableStatus?.Value)}");

        // Sent right away, with no wait, while the first change is still being applied.
        var (code, message) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        }));
        var want = (
            "ResourceInUseException",
            "Attempt to change a resource which is still in use: Table IOPS are currently being "
            + $"updated. Table: {table}");
        Ensure(
            (code, message) == want,
            $"a second UpdateTable while the first was UPDATING gave {Py.Repr(code)}: {Py.Repr(message)}, expected {Py.Repr(want)}");

        // The in-flight change settles at exactly 3/3; the poll times out (and fails) otherwise.
        await PollUntil(
            ddb,
            table,
            d => d.TableStatus == TableStatus.ACTIVE
                && (d.ProvisionedThroughput.ReadCapacityUnits, d.ProvisionedThroughput.WriteCapacityUnits) == (3L, 3L),
            timeoutS: 280,
            description: "ACTIVE at 3 read / 3 write");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll DescribeTable every 2 s for <paramref name="table"/> until <paramref name="predicate"/> holds.</summary>
    private static async Task<TableDescription> PollUntil(IAmazonDynamoDB ddb, string table, Func<TableDescription, bool> predicate, double timeoutS, string description)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (predicate(desc))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} never reached {description}; last {desc?.TableStatus?.Value}");
    }

    /// <summary>Wait out any in-flight UPDATING before DeleteTable, so a mid-check failure still cleans up.</summary>
    private static async Task DeleteOnceActive(IAmazonDynamoDB ddb, string table)
    {
        await PollUntil(ddb, table, d => d.TableStatus == TableStatus.ACTIVE, timeoutS: 280, description: "ACTIVE");
        await ddb.DeleteTableAsync(table);
    }
}
