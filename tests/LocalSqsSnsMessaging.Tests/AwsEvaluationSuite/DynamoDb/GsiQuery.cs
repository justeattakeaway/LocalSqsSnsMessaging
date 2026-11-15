// Translated from behavioral/dynamodb/check_gsi_query.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQuery
{
    private const string CheckId = "gsi-query";

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

    /// <summary>Query on a GSI returns items indexed by the GSI key.</summary>
    [Test]
    [DisplayName("dynamodb.gsi-query")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("email", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "email-index",
                    KeySchema = [new KeySchemaElement("email", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("u1")), ("email", S("alice@example.com")), ("name", S("Alice"))));
        await ddb.PutItemAsync(table, Item(("pk", S("u2")), ("email", S("bob@example.com")), ("name", S("Bob"))));
        var resp = await PollUntil(
            probe: () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "email-index",
                KeyConditionExpression = "email = :e",
                ExpressionAttributeValues = Item((":e", S("alice@example.com"))),
            }),
            predicate: r => r.Count == 1,
            description: "GSI propagation of the put item");
        Ensure(resp.Count == 1, $"expected exactly 1 item back from the GSI query, got {resp.Count}: {Py.Repr(resp.Items)}");
        var item = resp.Items[0];
        Ensure(Py.Equal(item["name"], S("Alice")), $"expected the GSI query to return Alice's item, got {Py.Repr(item)}");
    }
}
