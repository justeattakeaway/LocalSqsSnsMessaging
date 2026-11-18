// Translated from behavioral/dynamodb/check_scan_filter_ne_and_not_contains_on_missing_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-ne-and-not-contains-on-missing-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterNeAndNotContainsOnMissingAttribute
{
    private const string Check = "scan-filter-ne-and-not-contains-on-missing-attribute";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static Dictionary<string, Condition> Attr(ComparisonOperator op, params AttributeValue[] values) =>
        new(StringComparer.Ordinal)
        {
            ["attr"] = values.Length == 0
                ? new Condition { ComparisonOperator = op }
                : new Condition { ComparisonOperator = op, AttributeValueList = [.. values] },
        };

    /// <summary>
    /// Legacy ScanFilter NE and NULL match a missing attribute; NOT_NULL and NOT_CONTAINS do not.
    /// NOT_CONTAINS also diverges from its FilterExpression equivalent, 'NOT contains()', which does.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-ne-and-not-contains-on-missing-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-scanmiss-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 30);
        await ddb.PutItemAsync(table, Item(("id", S("has-attr")), ("attr", S("hello"))));
        await ddb.PutItemAsync(table, Item(("id", S("no-attr"))));

        async Task<List<string>> ScanIds(
            Dictionary<string, Condition>? scanFilter = null,
            string? filterExpression = null,
            Dictionary<string, string>? names = null,
            Dictionary<string, AttributeValue>? values = null)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                ScanFilter = scanFilter,
                FilterExpression = filterExpression,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            });
            return [.. resp.Items.Select(item => item["id"].S).Order(StringComparer.Ordinal)];
        }

        Dictionary<string, string> NamesA() => Names(("#a", "attr"));

        // NE: the attribute's absence is treated as "not equal", so the missing item matches and
        // the item carrying the equal value is excluded -- a positive control that NE is not
        // vacuously true for everything.
        var ne = await ScanIds(scanFilter: Attr(ComparisonOperator.NE, S("hello")));
        Ensure(ne.SequenceEqual(["no-attr"]), $"ScanFilter NE on a missing attribute returned {Py.Repr(ne)}, expected only the item without the attribute");
        var neExpr = await ScanIds(filterExpression: "#a <> :v", names: NamesA(), values: Item((":v", S("hello"))));
        Ensure(neExpr.SequenceEqual(ne), $"FilterExpression '<>' returned {Py.Repr(neExpr)}, must match legacy NE's {Py.Repr(ne)}");

        // NOT_CONTAINS: the attribute's absence is treated as no match, the opposite convention
        // from NE -- the missing item is excluded, same as the item whose value does contain the
        // substring.
        var notContains = await ScanIds(scanFilter: Attr(ComparisonOperator.NOT_CONTAINS, S("hel")));
        Ensure(notContains.Count == 0, $"ScanFilter NOT_CONTAINS on a missing attribute returned {Py.Repr(notContains)}, expected no items");

        // The FilterExpression form built from attribute_exists's own complement -- NOT contains() --
        // is NOT the same function: contains() evaluates false for a missing attribute, so its
        // negation matches it, unlike the legacy operator above.
        var notContainsExpr = await ScanIds(filterExpression: "NOT contains(#a, :v)", names: NamesA(), values: Item((":v", S("hel"))));
        Ensure(
            notContainsExpr.SequenceEqual(["no-attr"]),
            $"'NOT contains(#a, :v)' returned {Py.Repr(notContainsExpr)}, expected the missing-attribute item -- it diverges from legacy NOT_CONTAINS, which excluded it");

        // NOT_NULL/NULL mean exists/not-exists, matching attribute_exists()/attribute_not_exists().
        var notNull = await ScanIds(scanFilter: Attr(ComparisonOperator.NOT_NULL));
        var @null = await ScanIds(scanFilter: Attr(ComparisonOperator.NULL));
        Ensure(notNull.SequenceEqual(["has-attr"]), $"ScanFilter NOT_NULL returned {Py.Repr(notNull)}, expected only the item carrying the attribute");
        Ensure(@null.SequenceEqual(["no-attr"]), $"ScanFilter NULL returned {Py.Repr(@null)}, expected only the item missing the attribute");
        var have = await ScanIds(filterExpression: "attribute_exists(#a)", names: NamesA());
        var lack = await ScanIds(filterExpression: "attribute_not_exists(#a)", names: NamesA());
        Ensure(have.SequenceEqual(notNull), $"attribute_exists() returned {Py.Repr(have)}, must match ScanFilter NOT_NULL's {Py.Repr(notNull)}");
        Ensure(lack.SequenceEqual(@null), $"attribute_not_exists() returned {Py.Repr(lack)}, must match ScanFilter NULL's {Py.Repr(@null)}");

        // NULL and NOT_NULL are the two legacy operators documented to take no AttributeValueList;
        // supplying one is rejected, not silently ignored.
        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, ScanFilter = Attr(ComparisonOperator.NULL, S("x")) }));
        Ensure(code == "ValidationException", $"expected ValidationException supplying a value to NULL, got {code}: {Py.Repr(message)}");
        Ensure(
            message == "One or more parameter values were invalid: Invalid number of argument(s) for the NULL ComparisonOperator",
            $"unexpected message {Py.Repr(message)}");
    }
}
