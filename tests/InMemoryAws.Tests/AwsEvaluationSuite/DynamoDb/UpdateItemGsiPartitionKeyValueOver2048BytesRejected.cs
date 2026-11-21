// Translated from behavioral/dynamodb/check_update_item_gsi_partition_key_value_over_2048_bytes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-gsi-partition-key-value-over-2048-bytes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemGsiPartitionKeyValueOver2048BytesRejected
{
    private const string Check = "update-item-gsi-partition-key-value-over-2048-bytes-rejected";

    private static readonly (string Code, string Message) OverLimit = (
        "ValidationException",
        "One or more parameter values were invalid: Size limit exceeded for Index Key gpk " +
        "Actual Size: 2049 bytes Max Size: 2048 bytes IndexName: gsi1");

    /// <summary>A cleanup running undo() where a failed delete raises; the resource's own not-found code means it is already gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, FidelityStopwatch clock, double boundSeconds)
    {
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < boundSeconds)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE with its GSI before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>The (code, message) <paramref name="fn"/> raises; an unexpected success raises instead.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected a refusal, but the call succeeded: {result}") : (err.Code, err.Message);
    }

    private static async Task<QueryResponse> PollQuery(Func<Task<QueryResponse>> query, Func<QueryResponse, bool> pred, FidelityStopwatch clock, double boundSeconds)
    {
        var result = await query();
        while (!pred(result) && clock.Elapsed.TotalSeconds < boundSeconds)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            result = await query();
        }

        return result;
    }

    /// <summary>
    /// UpdateItem refuses to create a new item whose GSI partition-key attribute is over
    /// 2048 bytes, leaving no item in the base table, while exactly 2048 bytes is accepted and
    /// queryable on the index.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Limits.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-gsi-partition-key-value-over-2048-bytes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "The check's deadline is the runner's time left minus 60 s, which is nothing under the 60-second emulator budget, so the ACTIVE wait gives up at once")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgks-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 600.0 : left) - 60.0);
        var clock = FidelityTime.StartNew();
        await WaitActive(ddb, table, clock, bound);

        var (code, message) = await CodeAndMessage(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("over"))),
            UpdateExpression = "SET gpk = :g",
            ExpressionAttributeValues = Item((":g", S(new string('g', 2049)))),
        }));
        Ensure(
            (code, message) == OverLimit,
            $"a 2049-byte GSI partition-key value was refused as {Py.Repr((code, message))}, expected {Py.Repr(OverLimit)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("over"))), ConsistentRead = true })).Item is null,
            "a new item refused for its GSI key's size must not be created in the base table");

        var okValue = new string('g', 2048);
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ok"))),
            UpdateExpression = "SET gpk = :g",
            ExpressionAttributeValues = Item((":g", S(okValue))),
        });
        var found = await PollQuery(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "gsi1",
                KeyConditionExpression = "gpk = :g",
                ExpressionAttributeValues = Item((":g", S(okValue))),
            }),
            r => r.Count == 1,
            clock,
            bound);
        Ensure(found.Count == 1, $"a 2048-byte GSI partition-key value never appeared on gsi1: {Py.Repr(found.Items)}");
        Ensure(Py.Equal(found.Items[0]["pk"], S("ok")), $"gsi1 returned the wrong item: {Py.Repr(found.Items[0])}");
    }
}
