// Translated from behavioral/dynamodb/check_put_item_omitting_a_gsi_key_is_a_sparse_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-omitting-a-gsi-key-is-a-sparse-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemOmittingAGsiKeyIsASparseWrite
{
    /// <summary>
    /// An item missing the GSI key is ACCEPTED and simply does not appear in the index -- sparse
    /// indexes are a modelled feature, so rejecting the write is over-strict.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-omitting-a-gsi-key-is-a-sparse-write")]
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
                new AttributeDefinition("lsk", ScalarAttributeType.N),
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
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("sparse")), ("sk", S("1")), ("lsk", N("1"))));
        var sparse = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("sparse")), ("sk", S("1"))),
            ConsistentRead = true,
        })).Item;
        Ensure(sparse is { Count: > 0 }, "the sparse item must exist in the table");

        // Positive control: an item that carries the GSI key is indexed (GSIs are eventually
        // consistent, so poll for it), and the index then holds that item and not the sparse one.
        await ddb.PutItemAsync(table, Item(("pk", S("dense")), ("sk", S("1")), ("lsk", N("1")), ("gpk", S("indexed"))));
        var timeLeft = ctx.TimeLeftSeconds;
        var deadlineS = Math.Max(1.0, Math.Min(60.0, (timeLeft == 0 ? 60.0 : timeLeft) - 10.0));
        var clock = FidelityTime.StartNew();
        var query = new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gpk = :v",
            ExpressionAttributeValues = Item((":v", S("indexed"))),
        };
        var res = await ddb.QueryAsync(query);
        while (res.Count < 1 && clock.Elapsed.TotalSeconds < deadlineS)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            res = await ddb.QueryAsync(query);
        }

        Ensure(res.Count == 1, $"the item carrying gpk must appear in gsi1, got {Py.Repr(res.Items)}");
        var scanned = (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = "gsi1" })).Items
            .Select(i => i["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(scanned.SequenceEqual(["dense"]), $"gsi1 must hold only the item with a GSI key, got {Py.Repr(scanned)}");
    }
}
