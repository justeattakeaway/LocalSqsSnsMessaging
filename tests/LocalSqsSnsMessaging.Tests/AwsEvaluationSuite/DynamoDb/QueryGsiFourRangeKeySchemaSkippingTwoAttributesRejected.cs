// Translated from behavioral/dynamodb/check_query_gsi_four_range_key_schema_skipping_two_attributes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-gsi-four-range-key-schema-skipping-two-attributes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryGsiFourRangeKeySchemaSkippingTwoAttributesRejected
{
    private const string Check = "query-gsi-four-range-key-schema-skipping-two-attributes-rejected";
    private const string ErrValidation = "ValidationException";
    private const string Index = "by-four-range";
    private static readonly string[] Range = ["gsk1", "gsk2", "gsk3", "gsk4"];

    /// <summary>The (code, message) <paramref name="fn"/> is refused with. An unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>read() every 2 s until it equals want, bounded by the run budget.</summary>
    private static async Task<List<string>> Poll(FidelityContext ctx, Func<Task<List<string>>> read, List<string> want)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 120.0 : left) - 20.0));
        while (true)
        {
            var value = await read();
            if (value.SequenceEqual(want) || clock.Elapsed >= deadline)
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static List<Dictionary<string, string>> Boto(IEnumerable<KeySchemaElement> schema) =>
        [.. schema.Select(k => new Dictionary<string, string>(StringComparer.Ordinal) { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType.Value })];

    /// <summary>
    /// A GSI keyed on one HASH and four RANGE attributes requires an equality condition on every
    /// RANGE attribute before the last one referenced; skipping several at once is ValidationException
    /// naming every skipped attribute, while the full equality prefix still returns the item.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbMultiAttributeKeys, "CreateTable refuses a GSI key schema with four RANGE attributes")]
    [DisplayName("dynamodb.query-gsi-four-range-key-schema-skipping-two-attributes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsi4rx-");
        List<KeySchemaElement> keySchema = [new("gpk", KeyType.HASH), .. Range.Select(n => new KeySchemaElement(n, KeyType.RANGE))];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [.. new[] { "pk", "gpk" }.Concat(Range).Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = keySchema,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var desc = await WaitActive(ddb, ctx, table);
        var described = Boto(desc.GlobalSecondaryIndexes[0].KeySchema);
        Ensure(Py.Equal(described, Boto(keySchema)), $"the GSI describes its key as {Py.Repr(described)}");

        await ddb.PutItemAsync(table, Item(
            ("pk", S("p1")), ("gpk", S("g1")),
            ("gsk1", S("a")), ("gsk2", S("b")), ("gsk3", S("c")), ("gsk4", S("d"))));

        Task<QueryResponse> Query(string keyConditionExpression, Dictionary<string, AttributeValue> values) =>
            ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                KeyConditionExpression = keyConditionExpression,
                ExpressionAttributeValues = values,
            });

        async Task<List<string>> Pks(string keyConditionExpression, Dictionary<string, AttributeValue> values)
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var page = await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = Index,
                    KeyConditionExpression = keyConditionExpression,
                    ExpressionAttributeValues = values,
                    ExclusiveStartKey = start,
                });
                items.AddRange(page.Items);
                start = page.LastEvaluatedKey;
            }
            while (start is { Count: > 0 });

            return [.. items.Select(i => i["pk"].S).Order(StringComparer.Ordinal)];
        }

        // Conditioning on gsk1 and gsk4 while skipping both gsk2 and gsk3 (in between) names every
        // attribute that must have an equality condition before gsk4 can carry one.
        var (code, message) = await Error(() => Query(
            "gpk = :g AND gsk1 = :v1 AND gsk4 = :v4",
            Item((":g", S("g1")), (":v1", S("a")), (":v4", S("d")))));
        Ensure(code == ErrValidation, $"skipping gsk2 and gsk3 gave {Py.Repr(code)}");
        var expected =
            "RANGE key attributes gsk2, gsk3 must have equality conditions specified in the query " +
            "because a condition is present on key attribute gsk4";
        Ensure(message == expected, $"skipping gsk2 and gsk3: expected {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Conditioning only on gsk4, with no condition at all on gsk1, gsk2 or gsk3, names all three.
        (code, message) = await Error(() => Query(
            "gpk = :g AND gsk4 = :v4",
            Item((":g", S("g1")), (":v4", S("d")))));
        Ensure(code == ErrValidation, $"conditioning only on gsk4 gave {Py.Repr(code)}");
        expected =
            "RANGE key attributes gsk1, gsk2, gsk3 must have equality conditions specified in the " +
            "query because a condition is present on key attribute gsk4";
        Ensure(message == expected, $"conditioning only on gsk4: expected {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Positive control: the full equality prefix still returns the item after both rejections.
        var got = await Poll(ctx, () => Pks(
            "gpk = :g AND gsk1 = :v1 AND gsk2 = :v2 AND gsk3 = :v3 AND gsk4 = :v4",
            Item((":g", S("g1")), (":v1", S("a")), (":v2", S("b")), (":v3", S("c")), (":v4", S("d")))), ["p1"]);
        Ensure(got.SequenceEqual(["p1"]), $"the full equality prefix after the rejections selected {Py.Repr(got)}");
    }
}
