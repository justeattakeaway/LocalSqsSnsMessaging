// Translated from behavioral/dynamodb/check_online_gsi_add_echoes_include_projection_with_composite_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.online-gsi-add-echoes-include-projection-with-composite-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class OnlineGsiAddEchoesIncludeProjectionWithCompositeKey
{
    private const string Check = "online-gsi-add-echoes-include-projection-with-composite-key";
    private const string IncludeGsi = "gsi-include-composite";
    private const double WaitS = 1500.0;

    /// <summary>
    /// A GSI added online with UpdateTable is described with its INCLUDE projection's
    /// NonKeyAttributes and its composite (hash+range) KeySchema exactly as declared, and
    /// backfills an item that existed before the index did, projecting only the named attribute.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.online-gsi-add-echoes-include-projection-with-composite-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-onl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // An item carrying every attribute the index will divide up, written before the index
        // exists so the add must backfill it rather than needing a fresh write.
        await ddb.PutItemAsync(t, Item(("pk", S("p")), ("g1", S("a1")), ("g2", S("a2")), ("extra", S("EX")), ("excluded", S("NO"))));

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = t,
            AttributeDefinitions = [new AttributeDefinition("g1", ScalarAttributeType.S), new AttributeDefinition("g2", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = IncludeGsi,
                        KeySchema = [new KeySchemaElement("g1", KeyType.HASH), new KeySchemaElement("g2", KeyType.RANGE)],
                        Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["extra"] },
                    },
                },
            ],
        });

        var settled = await Poll(
            async () => (await ddb.DescribeTableAsync(t)).Table,
            tbl => Index(tbl, IncludeGsi).IndexStatus == IndexStatus.ACTIVE,
            timeoutS: WaitS,
            description: $"{IncludeGsi} ACTIVE");
        var includeGsi = Index(settled, IncludeGsi);
        var projection = new Dictionary<string, object?>
        {
            ["ProjectionType"] = includeGsi.Projection?.ProjectionType?.Value,
            ["NonKeyAttributes"] = includeGsi.Projection?.NonKeyAttributes,
        };
        Ensure(
            includeGsi.Projection?.ProjectionType == ProjectionType.INCLUDE
                && (includeGsi.Projection.NonKeyAttributes ?? []).SequenceEqual(["extra"]),
            "an online-added INCLUDE GSI must be described with its NonKeyAttributes, got " +
            $"{Py.Repr(projection)}");
        var keySchema = includeGsi.KeySchema.Select(k => (k.AttributeName, k.KeyType.Value)).ToList();
        Ensure(
            keySchema.SequenceEqual([("g1", "HASH"), ("g2", "RANGE")]),
            $"expected the composite hash+range KeySchema as declared, got {Py.Repr(keySchema.Select(k => new Dictionary<string, object?> { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.Value }).ToList())}");

        var includeResp = await Poll(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                IndexName = IncludeGsi,
                KeyConditionExpression = "g1 = :g1 AND g2 = :g2",
                ExpressionAttributeValues = Item((":g1", S("a1")), (":g2", S("a2"))),
            }),
            r => r.Count == 1,
            timeoutS: 120,
            intervalS: 2.0,
            description: "item backfilled into " + IncludeGsi);
        var includeItem = includeResp.Items[0];
        var names = includeItem.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(
            names.SequenceEqual(["extra", "g1", "g2", "pk"]),
            "an online INCLUDE GSI must project the table key, its own composite key and only the " +
            $"named NonKeyAttributes, got {Py.Repr(names)}");
        Ensure(Py.Equal(includeItem["extra"], S("EX")), Py.Repr(includeItem));
    }

    /// <summary>Poll <paramref name="fn"/> until <paramref name="pred"/> holds, never a fixed sleep standing in for the wait.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 5.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{description} not met within {timeoutS}s. Last: {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    private static GlobalSecondaryIndexDescription Index(TableDescription table, string name) =>
        (table.GlobalSecondaryIndexes ?? []).FirstOrDefault(g => g.IndexName == name)
        ?? throw Fail($"{name} missing from DescribeTable: {Py.Repr(table.GlobalSecondaryIndexes?.Select(g => g.IndexName).ToList())}");
}
