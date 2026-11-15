// Translated from behavioral/dynamodb/check_gsi_projection_keys_only.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-projection-keys-only</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiProjectionKeysOnly
{
    private const string CheckId = "gsi-projection-keys-only";

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 30, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>GSI with KEYS_ONLY projection returns only key attributes.</summary>
    [Test]
    [DisplayName("dynamodb.gsi-projection-keys-only")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("region", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "region-index",
                    KeySchema = [new KeySchemaElement("region", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("ko-1")), ("region", S("us-east-1")), ("extra", S("data"))));
        var resp = await PollUntil(
            probe: () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "region-index",
                KeyConditionExpression = "#r = :v",
                ExpressionAttributeNames = Names(("#r", "region")),
                ExpressionAttributeValues = Item((":v", S("us-east-1"))),
            }),
            predicate: r => r.Count == 1,
            timeoutSeconds: 30,
            description: "GSI propagation of the put item to region-index");
        Ensure(resp.Count == 1, $"expected exactly one item back from the region-index query, got Count={Py.Repr(resp.Count)}");
        var item = resp.Items[0];
        Ensure(
            item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "region"]),
            $"a KEYS_ONLY projection must return exactly the base table's key ('pk') and the index key ('region') and nothing else, got attribute set {Py.Repr(item.Keys.Order(StringComparer.Ordinal).ToList())}");
    }
}
