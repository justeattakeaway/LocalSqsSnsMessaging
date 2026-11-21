// Translated from behavioral/dynamodb/check_query_gsi_two_hash_one_range_key_exclusive_start_key_missing_an_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-gsi-two-hash-one-range-key-exclusive-start-key-missing-an-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryGsiTwoHashOneRangeKeyExclusiveStartKeyMissingAnAttributeRejected
{
    private const string Check = "query-gsi-two-hash-one-range-key-exclusive-start-key-missing-an-attribute-rejected";
    private const string IndexName = "by-tenant-region";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// A GSI Query's ExclusiveStartKey missing the table's key attribute or any one of the
    /// index's two HASH and one RANGE attributes is rejected with ValidationException, and the
    /// table's contents and a complete key's continuation are unchanged by the rejections.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-gsi-two-hash-one-range-key-exclusive-start-key-missing-an-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsi2h1rneg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
                new AttributeDefinition("region", ScalarAttributeType.S),
                new AttributeDefinition("ts", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = IndexName,
                    KeySchema =
                    [
                        new KeySchemaElement("tenant", KeyType.HASH),
                        new KeySchemaElement("region", KeyType.HASH),
                        new KeySchemaElement("ts", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"item-{i:000}")), ("tenant", S("acme")), ("region", S("eu")), ("ts", S($"{i:000}"))));
        }

        // The GSI is eventually consistent: poll until the index holds all five items.
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 60.0);
        while ((await Query(ddb, table)).Count != 5)
        {
            Ensure(clock.Elapsed.TotalSeconds < deadline, "the index never held all five items");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var page = await Query(ddb, table, limit: 1);
        Ensure(page.Items.Select(i => i["pk"].S).SequenceEqual(["item-000"]), Py.Repr(page.Items));
        var realKey = page.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        Ensure(
            Py.Equal(realKey, Item(("pk", S("item-000")), ("tenant", S("acme")), ("region", S("eu")), ("ts", S("000")))),
            $"expected the first item's four key attributes as the LastEvaluatedKey, got {Py.Repr(realKey)}");

        // Positive control: the real, complete ExclusiveStartKey continues the walk.
        var cont = await Query(ddb, table, exclusiveStartKey: realKey);
        var contIds = cont.Items.Select(i => i["pk"].S).ToList();
        Ensure(
            contIds.SequenceEqual(Enumerable.Range(1, 4).Select(i => $"item-{i:000}")),
            $"positive control: the real ExclusiveStartKey must resume after item-000, got {Py.Repr(contIds)}");

        foreach (var missing in new[] { "pk", "tenant", "region", "ts" })
        {
            var badKey = realKey!.Where(e => e.Key != missing).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            var (code, message) = await Error(() => Query(ddb, table, exclusiveStartKey: badKey));
            Ensure(
                code == ErrValidation,
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
            Ensure(
                message == "The provided starting key is invalid",
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected AWS's exact ValidationException " +
                $"message, got {Py.Repr(message)}");
        }

        // None of the four rejected calls wrote, deleted or otherwise changed the table.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        var finalIds = final.Select(i => i["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            finalIds.SequenceEqual(Enumerable.Range(0, 5).Select(i => $"item-{i:000}")),
            $"expected the original 5 items unchanged after the rejected calls, got {Py.Repr(finalIds)}");

        // A second positive control: the real key still continues the walk after the rejections.
        var cont2 = await Query(ddb, table, exclusiveStartKey: realKey);
        Ensure(
            Py.Equal(cont2.Items, cont.Items),
            "the real ExclusiveStartKey's continuation changed after the rejected calls: " +
            $"{Py.Repr(cont2.Items)} vs {Py.Repr(cont.Items)}");
    }

    private static Task<QueryResponse> Query(IAmazonDynamoDB ddb, string table, int? limit = null, Dictionary<string, AttributeValue>? exclusiveStartKey = null) =>
        ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = IndexName,
            KeyConditionExpression = "tenant = :t AND #r = :r",
            ExpressionAttributeNames = Names(("#r", "region")),
            ExpressionAttributeValues = Item((":t", S("acme")), (":r", S("eu"))),
            Limit = limit,
            ExclusiveStartKey = exclusiveStartKey,
        });

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutS = 300.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = last.GlobalSecondaryIndexes ?? [];
            if (last.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} or its GSI did not become ACTIVE: {Py.Repr(last?.TableStatus)}");
    }

    /// <summary>A cleanup running <paramref name="undo"/> where a failed delete raises; only <paramref name="notFound"/> means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary><c>(Code, Message)</c> for the error <paramref name="fn"/> raises; an unexpected success raises too.</summary>
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
