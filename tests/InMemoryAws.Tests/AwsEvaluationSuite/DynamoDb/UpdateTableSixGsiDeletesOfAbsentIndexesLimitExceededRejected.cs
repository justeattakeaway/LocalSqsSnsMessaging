// Translated from behavioral/dynamodb/check_update_table_six_gsi_deletes_of_absent_indexes_limit_exceeded_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-six-gsi-deletes-of-absent-indexes-limit-exceeded-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableSixGsiDeletesOfAbsentIndexesLimitExceededRejected
{
    private const string CheckId = "update-table-six-gsi-deletes-of-absent-indexes-limit-exceeded-rejected";

    private static readonly string[] AbsentNames = [.. Enumerable.Range(0, 6).Select(i => $"absent-idx-{i}")];

    /// <summary>The (Code, Message) fn() raises. An unexpected success raises, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, double deadlineS = 240)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadlineS)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never became ACTIVE within {deadlineS}s: {desc}");
    }

    /// <summary>
    /// UpdateTable deleting six GSIs the table never had answers the one-online-index-change
    /// LimitExceededException, while a single Delete of one absent GSI answers that index's own
    /// ResourceNotFoundException: the count is checked before the index lookup.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-six-gsi-deletes-of-absent-indexes-limit-exceeded-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsidelabs-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        var desc = await WaitActive(ddb, table);
        Ensure(desc.GlobalSecondaryIndexes is not { Count: > 0 }, $"the new table unexpectedly has GSIs {Py.Repr(desc.GlobalSecondaryIndexes)}");

        // Break exactly one thing: six Deletes, each naming a GSI the table never had.
        var (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [.. AbsentNames.Select(name => new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = name } })],
        }));
        var expectedLimit = (
            "LimitExceededException",
            "Subscriber limit exceeded: Only 1 online index can be created or deleted "
            + "simultaneously per table");
        Ensure((code, msg) == expectedLimit, $"six Deletes of six absent indexes gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedLimit)}");
        desc = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(desc.GlobalSecondaryIndexes is not { Count: > 0 }, $"after the refused bulk delete the table has GSIs {Py.Repr(desc.GlobalSecondaryIndexes)}");

        // Contrast: a lone Delete of one of the same absent names falls through the count check
        // and hits that index's own not-found error instead.
        (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = AbsentNames[0] } }],
        }));
        var expectedNotFound = (
            "ResourceNotFoundException",
            $"Requested resource not found: Index {AbsentNames[0]} for table {table}");
        Ensure(
            (code, msg) == expectedNotFound,
            $"a single Delete of the absent index {Py.Repr(AbsentNames[0])} gave {Py.Repr((code, msg))}, expected {Py.Repr(expectedNotFound)}");

        desc = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(desc.GlobalSecondaryIndexes is not { Count: > 0 }, $"after both refusals the table has GSIs {Py.Repr(desc.GlobalSecondaryIndexes)}");
    }
}
