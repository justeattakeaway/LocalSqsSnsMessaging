// Translated from behavioral/dynamodb/check_transact_write_cancellation_omits_capacity_and_collection_metrics.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-cancellation-omits-capacity-and-collection-metrics</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteCancellationOmitsCapacityAndCollectionMetrics
{
    private const string Check = "transact-write-cancellation-omits-capacity-and-collection-metrics";
    private static readonly (string Code, string Message) Canceled = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed, None]");

    /// <summary>
    /// A TransactWriteItems canceled by a failing ConditionCheck returns the ALL_OLD conflicting
    /// item, but the error omits ConsumedCapacity and ItemCollectionMetrics even though both were
    /// requested; a committed transaction on the same LSI-bearing table reports both.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-cancellation-omits-capacity-and-collection-metrics")]
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
                new AttributeDefinition("alt", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "alt-index",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("alt", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var existing = Item(
            ("pk", S("conflict")), ("sk", S("v1")), ("alt", S("a1")),
            ("version", N("1")), ("payload", S("original")));
        await ddb.PutItemAsync(table, existing);

        // The check reads the whole error response (exc.response), so this call goes out raw: the
        // .NET SDK's exception doesn't expose members of the error body beyond its own model.
        var response = await ExpectError(
            ctx,
            "TransactWriteItems",
            JsonSerializer.Serialize(new
            {
                ReturnConsumedCapacity = "INDEXES",
                ReturnItemCollectionMetrics = "SIZE",
                TransactItems = new object[]
                {
                    new
                    {
                        ConditionCheck = new
                        {
                            TableName = table,
                            Key = new { pk = new { S = "conflict" }, sk = new { S = "v1" } },
                            ConditionExpression = "version = :stale",
                            ExpressionAttributeValues = new Dictionary<string, object> { [":stale"] = new { N = "99" } },
                            ReturnValuesOnConditionCheckFailure = "ALL_OLD",
                        },
                    },
                    new
                    {
                        Put = new
                        {
                            TableName = table,
                            Item = new { pk = new { S = "other" }, sk = new { S = "v1" }, alt = new { S = "a2" } },
                        },
                    },
                },
            }),
            Canceled.Code,
            Canceled.Message);

        var reasons = response.TryGetProperty("CancellationReasons", out var r) ? r.EnumerateArray().ToList() : null;
        Ensure(
            reasons is not null && reasons.Count == 2,
            $"CancellationReasons must be positionally aligned with the 2 TransactItems, got {Py.Repr(reasons)}");
        var (failed, ok) = (reasons![0], reasons[1]);
        Ensure(Member(failed, "Code") is { } fc && fc.GetString() == "ConditionalCheckFailed", $"member 0 must report ConditionalCheckFailed, got {Py.Repr(failed)}");
        Ensure(
            Member(failed, "Message")?.GetString() == "The conditional request failed",
            $"member 0 must carry \"The conditional request failed\", got {Py.Repr(Member(failed, "Message"))}");
        Ensure(
            Py.Equal(Member(failed, "Item"), existing),
            "ReturnValuesOnConditionCheckFailure=ALL_OLD must return the complete conflicting item "
            + $"{Py.Repr(existing)}, got {Py.Repr(Member(failed, "Item"))}");
        Ensure(Member(ok, "Code")?.GetString() == "None", $"member 1 (not failing) must report Code \"None\", got {Py.Repr(ok)}");
        Ensure(Member(ok, "Item") is null, $"member 1 must not carry an Item, got {Py.Repr(Member(ok, "Item"))}");
        Ensure(Member(ok, "Message") is null, $"member 1 must not carry a Message, got {Py.Repr(Member(ok, "Message"))}");

        Ensure(
            Member(response, "ConsumedCapacity") is null,
            "a canceled TransactWriteItems error must omit ConsumedCapacity even when "
            + $"ReturnConsumedCapacity=INDEXES was requested, got {Py.Repr(Member(response, "ConsumedCapacity"))}");
        Ensure(
            Member(response, "ItemCollectionMetrics") is null,
            "a canceled TransactWriteItems error must omit ItemCollectionMetrics even when "
            + $"ReturnItemCollectionMetrics=SIZE was requested, got {Py.Repr(Member(response, "ItemCollectionMetrics"))}");

        var unchanged = await Get(ddb, table, "conflict");
        Ensure(
            Py.Equal(unchanged, existing),
            $"the conflicting item is {Py.Repr(unchanged)} after a canceled transaction, expected it unchanged at {Py.Repr(existing)}");
        Ensure(
            await Get(ddb, table, "other") is null,
            "the transaction was canceled, so the would-have-succeeded Put must not have written its item");

        // --- positive control: same table, a committing transaction with the same opt-ins set ---
        var committed = await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put { TableName = table, Item = Item(("pk", S("free")), ("sk", S("v1")), ("alt", S("a3"))) },
                },
            ],
        });
        var consumed = committed.ConsumedCapacity[0];
        Ensure(consumed.TableName == table, $"ConsumedCapacity must name the written table, got {Py.Repr(consumed.TableName)}");
        Ensure(
            consumed.WriteCapacityUnits == 3.0,
            "a committed TransactWriteItems Put projected into one LSI reported WriteCapacityUnits "
            + $"{Py.Repr(consumed.WriteCapacityUnits)}, expected 3.0");

        var metrics = committed.ItemCollectionMetrics[table];
        Ensure(metrics.Count == 1, $"a committed Put on an LSI-bearing table must report one ItemCollectionMetrics entry, got {metrics.Count}");
        var key = metrics[0].ItemCollectionKey;
        Ensure(Py.Equal(key, Item(("pk", S("free")))), $"ItemCollectionKey must carry the partition key value of the written item, got {Py.Repr(key)}");
        var sizeRange = metrics[0].SizeEstimateRangeGB;
        Ensure(
            sizeRange is not null && sizeRange.SequenceEqual([0.0, 1.0]),
            $"SizeEstimateRangeGB for a one-item collection must be [0.0, 1.0], got {Py.Repr(sizeRange)}");

        Ensure(
            Py.Equal(await Get(ddb, table, "free"), Item(("pk", S("free")), ("sk", S("v1")), ("alt", S("a3")))),
            "the positive control item was not committed as written");
    }

    private static JsonElement? Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S("v1"))), ConsistentRead = true })).Item;

    /// <summary>
    /// Sends the request, asserts it is refused with (code, message) and returns the error
    /// response body for inspection; raises on an unexpected success.
    /// </summary>
    private static async Task<JsonElement> ExpectError(FidelityContext ctx, string operation, string body, string code, string message)
    {
        var raw = await ctx.DynamoDbRaw(operation, body);
        if ((int)raw.StatusCode < 400)
        {
            throw Fail($"expected {code} ({Py.Repr(message)}), but the call succeeded: {raw.Body}");
        }

        Ensure(raw.ErrorCode == code, $"expected error code {Py.Repr(code)}, got {Py.Repr(raw.ErrorCode)}");
        Ensure(raw.ErrorMessage == message, $"expected message {Py.Repr(message)}, got {Py.Repr(raw.ErrorMessage)}");
        return raw.Json!.Value;
    }
}
