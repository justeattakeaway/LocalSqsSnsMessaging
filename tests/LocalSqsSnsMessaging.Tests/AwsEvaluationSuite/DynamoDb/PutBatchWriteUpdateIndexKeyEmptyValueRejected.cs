// Translated from behavioral/dynamodb/check_put_batch_write_update_index_key_empty_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-batch-write-update-index-key-empty-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutBatchWriteUpdateIndexKeyEmptyValueRejected
{
    private const string ErrValidation = "ValidationException";
    private static readonly string[] RejectedPks = ["put-gsi", "put-lsi", "batch-gsi", "batch-lsi"];

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
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

    private static string GsiMessage(string attr, string kind) =>
        "One or more parameter values are not valid. A value specified for a secondary " +
        "index key is not supported. The AttributeValue for a key attribute cannot contain " +
        $"an empty {kind} value. IndexName: gsi1, IndexKey: {attr}";

    private static string LsiMessage(string attr, string kind) =>
        "One or more parameter values are not valid. A value specified for a secondary " +
        "index key is not supported. The AttributeValue for a key attribute cannot contain " +
        $"an empty {kind} value. IndexName: lsi1, IndexKey: {attr}";

    private static string UpdateMessage(string kind) =>
        "One or more parameter values are not valid. The update expression attempted to " +
        "update a secondary index key to a value that is not supported. The AttributeValue " +
        $"for a key attribute cannot contain an empty {kind} value.";

    /// <summary>
    /// PutItem and BatchWriteItem reject a GSI/LSI key set to an empty string or binary value
    /// with ValidationException naming IndexName and IndexKey; UpdateItem rejects the identical
    /// write with the same code but a different message that names neither.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-batch-write-update-index-key-empty-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lrk", ScalarAttributeType.B),
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
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lrk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Positive control: a healthy item with both index keys populated is accepted and readable.
        await ddb.PutItemAsync(table, Item(("pk", S("control")), ("sk", S("1")), ("gpk", S("g")), ("lrk", B("l"))));
        var control = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("control")), ("sk", S("1"))),
            ConsistentRead = true,
        });
        Ensure(control.Item is not null, $"positive control PutItem did not persist: {Py.Repr(control.Item)}");

        // PutItem: empty-string GSI key, and empty-binary LSI key.
        var (code, message) = await Error(() => ddb.PutItemAsync(table, Item(("pk", S("put-gsi")), ("sk", S("1")), ("gpk", S("")))));
        Ensure(code == ErrValidation, $"PutItem/GSI: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        var putGsiMessage = GsiMessage("gpk", "string");
        Ensure(message == putGsiMessage, $"PutItem/GSI: expected {Py.Repr(putGsiMessage)}, got {Py.Repr(message)}");

        (code, message) = await Error(() => ddb.PutItemAsync(table, Item(("pk", S("put-lsi")), ("sk", S("1")), ("lrk", B([])))));
        Ensure(code == ErrValidation, $"PutItem/LSI: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(message == LsiMessage("lrk", "binary"), $"PutItem/LSI: got {Py.Repr(message)}");

        // BatchWriteItem: same two emptiness violations, each its own request item.
        (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("batch-gsi")), ("sk", S("1")), ("gpk", S(""))) } }],
            },
        }));
        Ensure(code == ErrValidation, $"BatchWriteItem/GSI: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message == putGsiMessage,
            $"BatchWriteItem/GSI: expected the same message as PutItem/GSI {Py.Repr(putGsiMessage)}, got {Py.Repr(message)}");

        (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("batch-lsi")), ("sk", S("1")), ("lrk", B([]))) } }],
            },
        }));
        Ensure(code == ErrValidation, $"BatchWriteItem/LSI: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(message == LsiMessage("lrk", "binary"), $"BatchWriteItem/LSI: got {Py.Repr(message)}");

        // UpdateItem: the identical violation on an item that already exists, through SET.
        await ddb.PutItemAsync(table, Item(("pk", S("upd")), ("sk", S("1"))));
        (code, var updateGsi) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd")), ("sk", S("1"))),
            UpdateExpression = "SET gpk = :g",
            ExpressionAttributeValues = Item((":g", S(""))),
        }));
        Ensure(code == ErrValidation, $"UpdateItem/GSI: expected {ErrValidation}, got {code}: {Py.Repr(updateGsi)}");
        Ensure(updateGsi == UpdateMessage("string"), $"UpdateItem/GSI: got {Py.Repr(updateGsi)}");

        (code, var updateLsi) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("upd")), ("sk", S("1"))),
            UpdateExpression = "SET lrk = :l",
            ExpressionAttributeValues = Item((":l", B([]))),
        }));
        Ensure(code == ErrValidation, $"UpdateItem/LSI: expected {ErrValidation}, got {code}: {Py.Repr(updateLsi)}");
        Ensure(updateLsi == UpdateMessage("binary"), $"UpdateItem/LSI: got {Py.Repr(updateLsi)}");

        // The comparison the scope asks for: UpdateItem's message names neither the index nor the
        // key, unlike PutItem's and BatchWriteItem's, though both report the same error code.
        Ensure(putGsiMessage != updateGsi, "PutItem and UpdateItem must not share one message");
        Ensure(
            !updateGsi!.Contains("IndexName", StringComparison.Ordinal) && !updateGsi.Contains("IndexKey", StringComparison.Ordinal),
            $"UpdateItem message unexpectedly names an index or key: {Py.Repr(updateGsi)}");
        Ensure(
            putGsiMessage.Contains("IndexName", StringComparison.Ordinal) && putGsiMessage.Contains("IndexKey", StringComparison.Ordinal),
            "PutItem message must name the index and the key");

        // Nothing any of the four rejected writes could leave behind, and the update left the
        // existing item's index keys unset rather than corrupted to the empty value.
        var held = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("upd")), ("sk", S("1"))), ConsistentRead = true })).Item;
        Ensure(!held.ContainsKey("gpk") && !held.ContainsKey("lrk"), $"the rejected updates mutated the item: {Py.Repr(held)}");
        foreach (var pk in RejectedPks)
        {
            var missing = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S("1"))), ConsistentRead = true });
            Ensure(missing.Item is null, $"the rejected write for {Py.Repr(pk)} was persisted: {Py.Repr(missing.Item)}");
        }
    }
}
