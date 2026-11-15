// Translated from behavioral/dynamodb/check_execute_statement_missing_literal_comparison_operators_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-missing-literal-comparison-operators-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementMissingLiteralComparisonOperatorsRejected
{
    private const string CheckId = "execute-statement-missing-literal-comparison-operators-rejected";
    private static readonly string[] Operators = ["=", "<>", "<"];

    /// <summary>Two items under an `id` (S) + `timestamp` (N) key; one carries the sparse `status` attribute.</summary>
    private static Dictionary<string, AttributeValue>[] SeedItems() =>
    [
        Item(("id", S("user-001")), ("timestamp", N("1700000000")), ("status", S("active")), ("name", S("John Doe"))),
        Item(("id", S("user-002")), ("timestamp", N("1700000001")), ("name", S("Jane Doe"))),
    ];

    private static readonly string[] All = [.. SeedItems().Select(i => i["id"].S).Order(StringComparer.Ordinal)];

    /// <summary>
    /// ExecuteStatement refuses `=`, `&lt;&gt;` and `&lt;` against the MISSING literal with one
    /// ValidationException naming IS as the only comparator for it.
    ///
    /// `=` against a real value still runs, and the table's items are unchanged after every refusal.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-missing-literal-comparison-operators-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx, "missop");
        await Seed(ctx, name);

        var refusal = ("ValidationException", "MISSING must be compared using IS operator");

        foreach (var op in Operators)
        {
            var got = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT id FROM \"{name}\" WHERE \"status\" {op} MISSING",
                ConsistentRead = true,
            }));
            Ensure(got == refusal, $"\"status\" {op} MISSING raised {Py.Repr(got)}, expected {Py.Repr(refusal)}");
        }

        var control = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT id FROM \"{name}\" WHERE \"status\" = 'active'",
            ConsistentRead = true,
        });
        var controlIds = Ids(control.Items);
        Ensure(
            controlIds.SequenceEqual(["user-001"]),
            $"\"status\" = 'active' returned {Py.Repr(Tuple(controlIds))}, expected only ('user-001',) -- the operator itself works, only MISSING triggers the refusal");

        var after = await Scan(ddb, name);
        var afterIds = Ids(after);
        Ensure(
            afterIds.SequenceEqual(All),
            $"the table holds {Py.Repr(Tuple(afterIds))} after the refused statements, expected the seed unchanged {Py.Repr(Tuple(All))}");
    }

    /// <summary>An on-demand table keyed by id (S) HASH + timestamp (N) RANGE.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx, string label)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName($"sim2real-ddbsfa-{label}-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH), new KeySchemaElement("timestamp", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S), new AttributeDefinition("timestamp", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary>Write the seed items in one BatchWriteItem.</summary>
    private static async Task Seed(FidelityContext ctx, string name)
    {
        var resp = await ctx.Dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [name] = SeedItems().Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } }).ToList() },
        });
        Ensure(resp.UnprocessedItems is { Count: 0 }, $"BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed while seeding");
    }

    /// <summary>Every item of a strongly consistent Scan, paginated.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> Scan(IAmazonDynamoDB ddb, string name)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest { TableName = name, ConsistentRead = true, ExclusiveStartKey = start is { Count: > 0 } ? start : null });
            items.AddRange(page.Items);
            start = page.LastEvaluatedKey;
            if (start is not { Count: > 0 })
            {
                return items;
            }
        }
    }

    private static List<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];

    /// <summary>A tuple's repr, as Python prints <c>('a', 'b')</c>.</summary>
    private static PyTuple Tuple(IEnumerable<string> values) => new([.. values]);

    private sealed class PyTuple(List<string> values)
    {
        public override string ToString() =>
            values.Count == 1 ? $"({Py.Repr(values[0])},)" : $"({string.Join(", ", values.Select(Py.Repr))})";
    }

    /// <summary>The (code, message) <c>fn</c> raises; an unexpected success raises instead.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
