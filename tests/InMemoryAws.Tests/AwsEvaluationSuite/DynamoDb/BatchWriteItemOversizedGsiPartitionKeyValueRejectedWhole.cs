// Translated from behavioral/dynamodb/check_batch_write_item_oversized_gsi_partition_key_value_rejected_whole.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-oversized-gsi-partition-key-value-rejected-whole</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemOversizedGsiPartitionKeyValueRejectedWhole
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// BatchWriteItem refuses the whole batch when one item's GSI partition key value is
    /// over the 2048-byte limit, and neither that item nor its well-formed sibling is written.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.EmulatorBudget, "the check waits until time_left() - 60 s, which is no time at all under the runner's 60-second emulator budget, so its ACTIVE wait gives up before its first DescribeTable")]
    [DisplayName("dynamodb.batch-write-item-oversized-gsi-partition-key-value-rejected-whole")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddboverk-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, (left == 0 ? 900.0 : left) - 60.0);
        await WaitActive(ddb, table, clock, deadline);

        // Positive control: a well-formed batch against this table and index succeeds, so a
        // rejection below is attributable to the oversized key and not to a broken setup.
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("control")), ("sk", S("1")), ("gpk", S("g-control"))) } }],
            },
        });
        var control = await Get(ddb, table, "control");
        Ensure(
            Py.Equal(control, Item(("pk", S("control")), ("sk", S("1")), ("gpk", S("g-control")))),
            $"positive control did not round-trip: {Py.Repr(control)}");

        var oversizedGpk = new string('x', 2049);
        var error = await FidelityContext.CaptureError(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("valid")), ("sk", S("1")), ("gpk", S("g-valid"))) } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("bad")), ("sk", S("1")), ("gpk", S(oversizedGpk))) } },
                ],
            },
        })) ?? throw Fail("expected the call to be rejected, but it succeeded");
        var (code, message) = (error.Code, error.Message);
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        const string expectedMessage = "One or more parameter values were invalid: Size limit exceeded for Index Key gpk "
            + "Actual Size: 2049 bytes Max Size: 2048 bytes IndexName: gsi1";
        Ensure(
            message == expectedMessage,
            $"the message must name the offending index key and sizes: expected {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        Ensure(await Get(ddb, table, "valid") is null, "the well-formed sibling in a rejected batch must not be written");
        Ensure(await Get(ddb, table, "bad") is null, "the oversized-key item must not be written");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S("1"))), ConsistentRead = true })).Item;

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, FidelityStopwatch clock, double deadline)
    {
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(i => i.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} did not reach ACTIVE with its GSI before the deadline");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
