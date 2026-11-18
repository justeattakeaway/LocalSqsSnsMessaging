// Translated from behavioral/dynamodb/check_batch_write_item_key_defects_message_diverges_from_put_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-key-defects-message-diverges-from-put-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemKeyDefectsMessageDivergesFromPutItem
{
    private const string Check = "batch-write-item-key-defects-message-diverges-from-put-item";

    /// <summary>
    /// BatchWriteItem's PutRequest and DeleteRequest reject a wrong-typed, list-typed or missing
    /// partition key with ValidationException and one fixed message, which differs from PutItem's
    /// defect-specific message for the same malformed item; nothing is written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-key-defects-message-diverges-from-put-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));

        const string batchGenericMessage = "The provided key element does not match the schema";

        var defects = new (string Label, Func<Dictionary<string, AttributeValue>> MalformedKeyOrItem, string PutItemSuffix)[]
        {
            ("wrong-typed", () => Item(("pk", N("1"))), "Type mismatch for key pk expected: S actual: N"),
            ("list-typed", () => Item(("pk", L(S("x")))), "Type mismatch for key pk expected: S actual: L"),
            ("missing", () => Item(("other", S("x"))), "Missing the key pk in the item"),
        };

        foreach (var (label, malformed, putItemSuffix) in defects)
        {
            var (putCode, putMessage) = await ErrorCodeAndMessage(() => ddb.PutItemAsync(table, malformed()));
            Ensure(putCode == "ValidationException", $"PutItem {label} partition key: expected ValidationException, got {Py.Repr(putCode)}");
            var expectedPutMessage = $"One or more parameter values were invalid: {putItemSuffix}";
            Ensure(
                putMessage == expectedPutMessage,
                $"PutItem {label} partition key: expected message {Py.Repr(expectedPutMessage)}, " +
                $"got {Py.Repr(putMessage)}");

            var (batchPutCode, batchPutMessage) = await ErrorCodeAndMessage(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = malformed() } }] },
            }));
            Ensure(
                batchPutCode == "ValidationException",
                $"BatchWriteItem PutRequest {label} partition key: expected ValidationException, " +
                $"got {Py.Repr(batchPutCode)}");
            Ensure(
                batchPutMessage == batchGenericMessage,
                $"BatchWriteItem PutRequest {label} partition key: expected message " +
                $"{Py.Repr(batchGenericMessage)}, got {Py.Repr(batchPutMessage)}");
            Ensure(
                batchPutMessage != putMessage,
                $"BatchWriteItem PutRequest {label} partition key: message {Py.Repr(batchPutMessage)} " +
                $"must diverge from PutItem's defect-specific message {Py.Repr(putMessage)}, not reuse it");

            var (batchDeleteCode, batchDeleteMessage) = await ErrorCodeAndMessage(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new() { [table] = [new WriteRequest { DeleteRequest = new DeleteRequest { Key = malformed() } }] },
            }));
            Ensure(
                batchDeleteCode == "ValidationException",
                $"BatchWriteItem DeleteRequest {label} partition key: expected ValidationException, " +
                $"got {Py.Repr(batchDeleteCode)}");
            Ensure(
                batchDeleteMessage == batchGenericMessage,
                $"BatchWriteItem DeleteRequest {label} partition key: expected message " +
                $"{Py.Repr(batchGenericMessage)}, got {Py.Repr(batchDeleteMessage)}");
            Ensure(
                batchDeleteMessage != putMessage,
                $"BatchWriteItem DeleteRequest {label} partition key: message " +
                $"{Py.Repr(batchDeleteMessage)} must diverge from PutItem's defect-specific message " +
                $"{Py.Repr(putMessage)}, not reuse it");
        }

        // Effect absent: none of the six rejected calls above wrote anything.
        var scan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(scan.Count == 0, $"rejected calls must leave the table empty, found {scan.Count} item(s)");

        // Positive control: a correctly-typed PutItem and a correctly-typed BatchWriteItem
        // PutRequest both succeed on the same table.
        var goodPk1 = FidelityContext.UniqueName("item-");
        await ddb.PutItemAsync(table, Item(("pk", S(goodPk1)), ("note", S("good-put"))));
        var got1 = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(goodPk1))), ConsistentRead = true });
        Ensure(got1.Item is not null, "positive control PutItem did not persist");
        Ensure(got1.Item["note"].S == "good-put", $"expected note='good-put', got {Py.Repr(got1.Item["note"].S)}");

        var goodPk2 = FidelityContext.UniqueName("item-");
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S(goodPk2)), ("note", S("good-batch"))) } }] },
        });
        var got2 = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(goodPk2))), ConsistentRead = true });
        Ensure(got2.Item is not null, "positive control BatchWriteItem PutRequest did not persist");
        Ensure(got2.Item["note"].S == "good-batch", $"expected note='good-batch', got {Py.Repr(got2.Item["note"].S)}");
    }

    /// <summary>
    /// Call fn(); return the botocore Error.Code and Error.Message. An unexpected SUCCESS
    /// raises instead of returning a falsy code, because an emulator ACCEPTING input real AWS
    /// refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }
}
