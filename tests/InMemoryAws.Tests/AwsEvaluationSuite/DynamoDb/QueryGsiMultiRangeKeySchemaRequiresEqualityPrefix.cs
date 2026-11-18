// Translated from behavioral/dynamodb/check_query_gsi_multi_range_key_schema_requires_equality_prefix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-gsi-multi-range-key-schema-requires-equality-prefix</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryGsiMultiRangeKeySchemaRequiresEqualityPrefix
{
    private const string Check = "query-gsi-multi-range-key-schema-requires-equality-prefix";
    private const string ErrValidation = "ValidationException";
    private const string Index = "by-three-range";

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

    /// <summary><c>read()</c> until <c>done(value)</c>, bounded by <c>ctx.time_left()</c>.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, FidelityContext ctx, double pollS)
    {
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            var left = ctx.TimeLeftSeconds;
            if (left <= pollS)
            {
                throw Fail($"condition not met before the budget ran out; last value {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollS));
        }
    }

    private static List<Dictionary<string, string>> Boto(IEnumerable<KeySchemaElement> schema) =>
        [.. schema.Select(k => new Dictionary<string, string>(StringComparer.Ordinal) { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType.Value })];

    /// <summary>
    /// A GSI keyed on one HASH and three RANGE attributes requires an equality condition on every
    /// RANGE attribute before the last one referenced; skipping or under-constraining one
    /// is ValidationException naming it, while the full equality prefix returns the matching item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-gsi-multi-range-key-schema-requires-equality-prefix")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-mr-");
        List<KeySchemaElement> indexKeySchema =
        [
            new("gpk", KeyType.HASH),
            new("gsk1", KeyType.RANGE),
            new("gsk2", KeyType.RANGE),
            new("gsk3", KeyType.RANGE),
        ];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [.. new[] { "pk", "gpk", "gsk1", "gsk2", "gsk3" }.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = indexKeySchema,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var desc = await Poll(
            async () => (await ddb.DescribeTableAsync(table)).Table,
            t => t.TableStatus == TableStatus.ACTIVE && t.GlobalSecondaryIndexes.All(g => g.IndexStatus == IndexStatus.ACTIVE),
            ctx,
            pollS: 5.0);
        var keySchema = Boto(desc.GlobalSecondaryIndexes[0].KeySchema);
        Ensure(Py.Equal(keySchema, Boto(indexKeySchema)), $"the GSI describes its key as {Py.Repr(keySchema)}");

        await ddb.PutItemAsync(table, Item(
            ("pk", S("p1")), ("gpk", S("g1")), ("gsk1", S("a")), ("gsk2", S("b")), ("gsk3", S("c"))));

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

        // Positive control: the full equality prefix (hash plus every RANGE attribute) returns the item.
        var got = await Poll(
            () => Pks(
                "gpk = :g AND gsk1 = :g1 AND gsk2 = :g2 AND gsk3 = :g3",
                Item((":g", S("g1")), (":g1", S("a")), (":g2", S("b")), (":g3", S("c")))),
            v => v.SequenceEqual(["p1"]),
            ctx,
            pollS: 2.0);
        Ensure(got.SequenceEqual(["p1"]), $"the full equality prefix selected {Py.Repr(got)}");

        // Skipping the first RANGE attribute (gsk1): a condition on gsk2 alone is refused, naming
        // the skipped attribute and the one whose presence required it.
        var (code, message) = await Error(() => Query("gpk = :g AND gsk2 = :g2", Item((":g", S("g1")), (":g2", S("b")))));
        Ensure(code == ErrValidation, $"skipping gsk1 gave {Py.Repr(code)}");
        var expected = "RANGE key attributes gsk1 must have equality conditions specified in the query because a condition is present on key attribute gsk2";
        Ensure(message == expected, $"skipping gsk1: expected {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Skipping a middle attribute: gsk1 and gsk3 are conditioned but gsk2 (in between) is not.
        (code, message) = await Error(() => Query(
            "gpk = :g AND gsk1 = :g1 AND gsk3 = :g3",
            Item((":g", S("g1")), (":g1", S("a")), (":g3", S("c")))));
        Ensure(code == ErrValidation, $"skipping gsk2 gave {Py.Repr(code)}");
        expected = "RANGE key attributes gsk2 must have equality conditions specified in the query because a condition is present on key attribute gsk3";
        Ensure(message == expected, $"skipping gsk2: expected {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Placing a range comparison on gsk1 (not the last RANGE attribute referenced) while gsk2
        // carries a condition too: gsk1's comparator must be equality, not merely present.
        (code, message) = await Error(() => Query(
            "gpk = :g AND gsk1 > :g1 AND gsk2 = :g2",
            Item((":g", S("g1")), (":g1", S("a")), (":g2", S("b")))));
        Ensure(code == ErrValidation, $"a non-equality comparator on gsk1 before gsk2 gave {Py.Repr(code)}");
        expected = "RANGE key attributes gsk1 must have equality conditions specified in the query because a condition is present on key attribute gsk2";
        Ensure(message == expected, $"a non-equality comparator on gsk1 before gsk2: expected {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Final control: a non-equality comparator IS allowed on the last RANGE attribute referenced,
        // and the full equality prefix still answers correctly after every rejection above.
        got = await Pks(
            "gpk = :g AND gsk1 = :g1 AND gsk2 = :g2 AND gsk3 > :g3",
            Item((":g", S("g1")), (":g1", S("a")), (":g2", S("b")), (":g3", S("a"))));
        Ensure(got.SequenceEqual(["p1"]), $"a range comparison on the last referenced RANGE attribute selected {Py.Repr(got)}");

        got = await Pks(
            "gpk = :g AND gsk1 = :g1 AND gsk2 = :g2",
            Item((":g", S("g1")), (":g1", S("a")), (":g2", S("b"))));
        Ensure(got.SequenceEqual(["p1"]), $"the equality prefix after the rejections selected {Py.Repr(got)}");
    }
}
