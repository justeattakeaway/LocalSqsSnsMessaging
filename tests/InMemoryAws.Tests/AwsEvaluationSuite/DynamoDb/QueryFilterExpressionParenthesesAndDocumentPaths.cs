// Translated from behavioral/dynamodb/check_query_filter_expression_parentheses_and_document_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-parentheses-and-document-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionParenthesesAndDocumentPaths
{
    private const string Check = "query-filter-expression-parentheses-and-document-paths";

    /// <summary>
    /// Five sort keys under one partition, as (sk, status, category, tags, meta_created).
    /// <c>MetaCreated</c> is null where the item's <c>meta</c> map omits the <c>created</c> key.
    /// </summary>
    private static readonly (string Sk, string Status, string Category, string[] Tags, string? MetaCreated)[] Rows =
    [
        ("a", "active", "A", ["x", "y"], "2024"),
        ("b", "pending", "B", ["z"], null),
        ("c", "active", "B", ["x"], "2025"),
        ("d", "inactive", "A", ["y", "x"], null),
        ("e", "active", "C", [], "2024"),
    ];

    private static Dictionary<string, AttributeValue> RowItem((string Sk, string Status, string Category, string[] Tags, string? MetaCreated) row)
    {
        var meta = row.MetaCreated is not null ? M(("created", S(row.MetaCreated))) : M(("other", S("1")));
        return Item(
            ("pk", S("grp")), ("sk", S(row.Sk)),
            ("status", S(row.Status)), ("category", S(row.Category)),
            ("tags", L([.. row.Tags.Select(S)])), ("meta", meta));
    }

    /// <summary>A fresh pk='grp' table with the five rows, under one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbqfp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [name] = [.. Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = RowItem(r) } })],
            },
        });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }

    /// <summary>The set of <c>sk</c> values a Query against <paramref name="table"/> returns under one filter form.</summary>
    private static async Task<SortedSet<string>> Query(
        IAmazonDynamoDB ddb, string table, string filterExpression, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
    {
        var allValues = Item((":p", S("grp")));
        foreach (var (k, v) in values)
        {
            allValues[k] = v;
        }

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            FilterExpression = filterExpression,
            ExpressionAttributeValues = allValues,
            ExpressionAttributeNames = names.Count > 0 ? names : null,
        });
        return new SortedSet<string>(resp.Items.Select(i => i["sk"].S), StringComparer.Ordinal);
    }

    private static SortedSet<string> Where(Func<(string Sk, string Status, string Category, string[] Tags, string? MetaCreated), bool> predicate) =>
        new(Rows.Where(predicate).Select(r => r.Sk), StringComparer.Ordinal);

    /// <summary>
    /// Query's FilterExpression parses a full-wrap, a nested grouping, per-condition
    /// parens and a parenthesized function call the same way Scan's does, and a nested
    /// map path or list-index operand, parenthesized or not, selects the same items.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-parentheses-and-document-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx);
        var names = Names(("#s", "status"), ("#c", "category"));

        var fullWrap = Where(r => r.Status == "active" && r.Category == "B");
        var got = await Query(ddb, table, "(#s = :active AND #c = :catB)", names, Item((":active", S("active")), (":catB", S("B"))));
        Ensure(got.SetEquals(fullWrap), $"full-wrap AND returned {Py.Repr(got)}, expected {Py.Repr(fullWrap)}");

        var nested = Where(r => (r.Status == "pending" || r.Category == "A") && r.Category == "B");
        got = await Query(ddb, table, "(#s = :pending OR #c = :catA) AND #c = :catB", names,
            Item((":pending", S("pending")), (":catA", S("A")), (":catB", S("B"))));
        Ensure(got.SetEquals(nested), $"nested grouping returned {Py.Repr(got)}, expected {Py.Repr(nested)}");

        var perCondition = Where(r => r.Status == "active" && r.Category == "C");
        got = await Query(ddb, table, "(#s = :active) AND (#c = :catC)", names, Item((":active", S("active")), (":catC", S("C"))));
        Ensure(got.SetEquals(perCondition), $"per-condition parens returned {Py.Repr(got)}, expected {Py.Repr(perCondition)}");

        var startsWithA = Where(r => r.Category.StartsWith('A'));
        got = await Query(ddb, table, "(begins_with(#c, :pfx))", Names(("#c", "category")), Item((":pfx", S("A"))));
        Ensure(got.SetEquals(startsWithA), $"parenthesized begins_with returned {Py.Repr(got)}, expected {Py.Repr(startsWithA)}");

        var created2024 = Where(r => r.MetaCreated == "2024");
        got = await Query(ddb, table, "meta.created = :y", [], Item((":y", S("2024"))));
        Ensure(got.SetEquals(created2024), $"nested map path returned {Py.Repr(got)}, expected {Py.Repr(created2024)}");
        got = await Query(ddb, table, "(meta.created = :y)", [], Item((":y", S("2024"))));
        Ensure(got.SetEquals(created2024), $"parenthesized nested map path returned {Py.Repr(got)}, expected {Py.Repr(created2024)}");

        var firstTagX = Where(r => r.Tags.Take(1).SequenceEqual(["x"]));
        got = await Query(ddb, table, "tags[0] = :x", [], Item((":x", S("x"))));
        Ensure(got.SetEquals(firstTagX), $"list-index path returned {Py.Repr(got)}, expected {Py.Repr(firstTagX)}");
        got = await Query(ddb, table, "(tags[0] = :x)", [], Item((":x", S("x"))));
        Ensure(got.SetEquals(firstTagX), $"parenthesized list-index path returned {Py.Repr(got)}, expected {Py.Repr(firstTagX)}");
    }
}
