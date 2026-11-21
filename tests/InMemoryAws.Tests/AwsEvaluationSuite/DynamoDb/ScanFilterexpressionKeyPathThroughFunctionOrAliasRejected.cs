// Translated from behavioral/dynamodb/check_scan_filterexpression_key_path_through_function_or_alias_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filterexpression-key-path-through-function-or-alias-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterexpressionKeyPathThroughFunctionOrAliasRejected
{
    private const string Check = "scan-filterexpression-key-path-through-function-or-alias-rejected";

    private const string ScalarMsg =
        "Key attributes must be scalars; list random access '[]' and map lookup '.' are " +
        "not allowed: Key: pk";

    // (FilterExpression, ExpressionAttributeNames) — every nested form a Scan can route
    // a document path into the hash key through: inside attribute_exists(), through an
    // alias, and through an aliased bracket index, plus a plain aliased comparison for
    // control.
    private static readonly (string Expr, bool AliasPk)[] NestedForms =
    [
        ("attribute_exists(pk.foo)", false),
        ("attribute_exists(pk[0])", false),
        ("attribute_exists(#a.foo)", true),
        ("attribute_exists(#a[0])", true),
        ("#a[0] = :v", true),
        ("#a.foo = :v", true),
    ];

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

    /// <summary>
    /// A Scan FilterExpression reaching a nested path into the hash key is refused as a
    /// non-scalar key, whether the path sits inside attribute_exists(), is bracket-indexed,
    /// or is reached through an alias; a whole-key or non-key filter still matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filterexpression-key-path-through-function-or-alias-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-scankeypath-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        static Dictionary<string, AttributeValue> ItemValue() => Item(("pk", S("p1")), ("sk", S("s1")), ("color", S("red")));
        var item = ItemValue();
        await ddb.PutItemAsync(table, ItemValue());

        // Positive controls: a plain and an aliased reference to the whole key attribute,
        // and a filter on a non-key attribute, all still match — the refusals below are
        // about the nested path, not about naming the key or about FilterExpression itself.
        var plain = await ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = "attribute_exists(pk)", ConsistentRead = true });
        Ensure(Py.Equal(plain.Items, new[] { item }), $"attribute_exists(pk) returned {Py.Repr(plain.Items)}, expected {Py.Repr(new[] { item })}");

        var aliased = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "#a = :v",
            ConsistentRead = true,
            ExpressionAttributeNames = Names(("#a", "pk")),
            ExpressionAttributeValues = Item((":v", S("p1"))),
        });
        Ensure(Py.Equal(aliased.Items, new[] { item }), $"#a = :v (pk aliased) returned {Py.Repr(aliased.Items)}, expected {Py.Repr(new[] { item })}");

        var nonKey = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "color = :c",
            ConsistentRead = true,
            ExpressionAttributeValues = Item((":c", S("red"))),
        });
        Ensure(Py.Equal(nonKey.Items, new[] { item }), $"color = :c returned {Py.Repr(nonKey.Items)}, expected {Py.Repr(new[] { item })}");

        foreach (var (expr, aliasPk) in NestedForms)
        {
            var request = new ScanRequest { TableName = table, FilterExpression = expr };
            if (expr.Contains(":v", StringComparison.Ordinal))
            {
                request.ExpressionAttributeValues = Item((":v", S("p1")));
            }

            if (aliasPk)
            {
                request.ExpressionAttributeNames = Names(("#a", "pk"));
            }

            var (code, msg) = await Error(() => ddb.ScanAsync(request));
            Ensure(
                code == "ValidationException" && msg == ScalarMsg,
                $"Scan FilterExpression {Py.Repr(expr)} gave {Py.Repr(code)}: {Py.Repr(msg)}, expected ValidationException: {Py.Repr(ScalarMsg)}");
        }

        var rows = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(Py.Equal(rows, new[] { item }), $"the table changed across the refused scans: {Py.Repr(rows)}");
    }
}
