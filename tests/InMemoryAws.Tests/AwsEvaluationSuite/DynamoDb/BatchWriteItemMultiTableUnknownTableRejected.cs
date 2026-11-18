// Translated from behavioral/dynamodb/check_batch_write_item_multi_table_unknown_table_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-multi-table-unknown-table-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemMultiTableUnknownTableRejected
{
    private const string Check = "batch-write-item-multi-table-unknown-table-rejected";

    /// <summary>
    /// A BatchWriteItem whose RequestItems map names one real table and one syntactically valid but
    /// nonexistent table is ResourceNotFoundException for the whole call; the real table gets no write.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-multi-table-unknown-table-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var realTable = FidelityContext.UniqueName("sim2real-behav-bwiunk-");
        var missingTable = FidelityContext.UniqueName("sim2real-behav-bwiunk-missing-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = realTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(realTable));
        await ddb.WaitForTableExists(realTable, delaySeconds: 2, maxAttempts: 250);

        // Positive control: BatchWriteItem over the real table alone works, so the rejection below is
        // attributable to the missing table and not to a broken request.
        var control = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [realTable] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("control")), ("v", S("kept"))) } }],
            },
        });
        Ensure(
            (control.UnprocessedItems ?? []).Count == 0,
            "positive control BatchWriteItem on the real table alone left unprocessed items: " +
            $"{Py.Repr(control.UnprocessedItems?.Keys)}");

        var (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [realTable] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("new")), ("v", S("should-not-land"))) } }],
                [missingTable] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("x")), ("v", S("y"))) } }],
            },
        }));
        Ensure(
            code == "ResourceNotFoundException",
            "BatchWriteItem naming one real table and one nonexistent table must fail with " +
            $"ResourceNotFoundException for the whole call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == "Requested resource not found", $"unexpected message {Py.Repr(message)}");

        var after = await ddb.GetItemAsync(new GetItemRequest { TableName = realTable, Key = Item(("pk", S("new"))), ConsistentRead = true });
        Ensure(after.Item is null, $"the refused BatchWriteItem must not have written to the real table, got {Py.Repr(after.Item)}");
        var controlItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = realTable, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(controlItem, Item(("pk", S("control")), ("v", S("kept")))),
            $"the real table's prior item must survive the refused call, got {Py.Repr(controlItem)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }
}
