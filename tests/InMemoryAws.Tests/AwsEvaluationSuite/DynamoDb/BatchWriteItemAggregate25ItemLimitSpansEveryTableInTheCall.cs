// Translated from behavioral/dynamodb/check_batch_write_item_aggregate_25_item_limit_spans_every_table_in_the_call.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-aggregate-25-item-limit-spans-every-table-in-the-call</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemAggregate25ItemLimitSpansEveryTableInTheCall
{
    private const string Check = "batch-write-item-aggregate-25-item-limit-spans-every-table-in-the-call";
    private const string ErrValidation = "ValidationException";
    private const string AggregateMessage = "Too many items requested for the BatchWriteItem call";

    /// <summary>
    /// BatchWriteItem's 25-request limit is counted across every table in the call: 13 + 12
    /// requests over two tables (25 total) is accepted whole, and 13 + 13 (26 total, neither table's
    /// own list over 25) is refused with a distinct aggregate-count message, writing neither table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-aggregate-25-item-limit-spans-every-table-in-the-call")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = FidelityContext.UniqueName("sim2real-behav-ddbbw25-a-");
        await CreateTable(ddb, tableA);
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(tableA), "ResourceNotFoundException"));

        var tableB = FidelityContext.UniqueName("sim2real-behav-ddbbw25-b-");
        await CreateTable(ddb, tableB);
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(tableB), "ResourceNotFoundException"));

        await ddb.WaitForTableExists(tableA, delaySeconds: 2, maxAttempts: 30);
        await ddb.WaitForTableExists(tableB, delaySeconds: 2, maxAttempts: 30);

        // Positive control: 13 requests on table_a plus 12 on table_b is exactly 25 in total and is
        // accepted whole, so the rejection below is attributable to the aggregate count alone.
        var control = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [tableA] = Puts("ok-a-", 13),
                [tableB] = Puts("ok-b-", 12),
            },
        });
        var controlUnprocessed = control.UnprocessedItems ?? [];
        Ensure(
            controlUnprocessed.Count == 0,
            "a 13+12 (25 total) batch across two tables must be accepted whole, got "
            + $"UnprocessedItems={Py.Repr(controlUnprocessed.Keys)}");
        Ensure(
            Py.Equal(await Get(ddb, tableA, "ok-a-0"), Item(("pk", S("ok-a-0")))),
            "the control batch's item in table_a did not land");
        Ensure(
            Py.Equal(await Get(ddb, tableB, "ok-b-0"), Item(("pk", S("ok-b-0")))),
            "the control batch's item in table_b did not land");

        // The subject: 13 + 13 = 26 requests, neither table's own list exceeding 25 on its own.
        var (result, error) = await FidelityContext.Capture(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [tableA] = Puts("over-a-", 13),
                [tableB] = Puts("over-b-", 13),
            },
        }));
        if (error is null)
        {
            throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {result}");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(
            code == ErrValidation,
            $"a 13+13 (26 total) batch across two tables must be refused with {ErrValidation}, "
            + $"got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == AggregateMessage,
            $"expected the aggregate-count message {Py.Repr(AggregateMessage)}, got {Py.Repr(message)}");

        Ensure(await Get(ddb, tableA, "over-a-0") is null, "the refused 26-item batch must not have written to table_a");
        Ensure(await Get(ddb, tableB, "over-b-0") is null, "the refused 26-item batch must not have written to table_b");
    }

    private static List<WriteRequest> Puts(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S($"{prefix}{i}"))) } })];

    private static Task CreateTable(IAmazonDynamoDB ddb, string table) => ddb.CreateTableAsync(new CreateTableRequest
    {
        TableName = table,
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
        BillingMode = BillingMode.PAY_PER_REQUEST,
        Tags = [new Tag { Key = "sim2real", Value = Check }],
    });

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
