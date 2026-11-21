// Translated from behavioral/dynamodb/check_scan_filter_not_contains_operand_type_matrix_legacy_and_expression.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-not-contains-operand-type-matrix-legacy-and-expression</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterNotContainsOperandTypeMatrixLegacyAndExpression
{
    private const string Check = "scan-filter-not-contains-operand-type-matrix-legacy-and-expression";

    private static SortedSet<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        new(items.Select(i => i["pk"].S), StringComparer.Ordinal);

    private static SortedSet<string> Minus(SortedSet<string> all, params string[] removed) =>
        new(all.Where(s => !removed.Contains(s, StringComparer.Ordinal)), StringComparer.Ordinal);

    private static SortedSet<string> Minus(SortedSet<string> all, SortedSet<string> removed) =>
        new(all.Where(s => !removed.Contains(s)), StringComparer.Ordinal);

    /// <summary>
    /// Legacy ScanFilter NOT_CONTAINS and FilterExpression NOT contains() with an S, N or B
    /// operand exclude only the type-matched items that contain it, over a population mixing S,
    /// SS, NS, B and BS attributes of the same digits.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-not-contains-operand-type-matrix-legacy-and-expression")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbnc-");
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

        await ddb.PutItemAsync(t, Item(("pk", S("s")), ("coll", S("5"))));
        await ddb.PutItemAsync(t, Item(("pk", S("ss")), ("coll", SS("5", "6"))));
        await ddb.PutItemAsync(t, Item(("pk", S("ns")), ("coll", NS("5", "6"))));
        await ddb.PutItemAsync(t, Item(("pk", S("b")), ("coll", B("5"))));
        await ddb.PutItemAsync(t, Item(("pk", S("bs")), ("coll", BS("5"u8.ToArray(), "6"u8.ToArray()))));
        var all = new SortedSet<string>(["s", "ss", "ns", "b", "bs"], StringComparer.Ordinal);

        async Task<SortedSet<string>> LegacyNotContains(AttributeValue value)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ConsistentRead = true,
                ScanFilter = new Dictionary<string, Condition>(StringComparer.Ordinal)
                {
                    ["coll"] = new Condition { ComparisonOperator = ComparisonOperator.NOT_CONTAINS, AttributeValueList = [value] },
                },
            });
            return Ids(resp.Items);
        }

        async Task<SortedSet<string>> ExprNotContains(AttributeValue value)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ConsistentRead = true,
                FilterExpression = "NOT contains(coll, :v)",
                ExpressionAttributeValues = Item((":v", value)),
            });
            return Ids(resp.Items);
        }

        var legacyS = await LegacyNotContains(S("5"));
        Ensure(
            legacyS.SetEquals(Minus(all, "s", "ss")),
            $"legacy NOT_CONTAINS with an S operand excluded {Py.Repr(Minus(all, legacyS))}, expected " +
            "only the S (substring) and SS (member) items excluded");
        var exprS = await ExprNotContains(S("5"));
        Ensure(
            exprS.SetEquals(legacyS),
            $"'NOT contains(coll, (S)5)' returned {Py.Repr(exprS)}, must match legacy " +
            $"NOT_CONTAINS's {Py.Repr(legacyS)} over a population with no missing attribute");

        var legacyN = await LegacyNotContains(N("5"));
        Ensure(
            legacyN.SetEquals(Minus(all, "ns")),
            $"legacy NOT_CONTAINS with an N operand excluded {Py.Repr(Minus(all, legacyN))}, expected " +
            "only the NS member item excluded");
        var exprN = await ExprNotContains(N("5"));
        Ensure(
            exprN.SetEquals(legacyN),
            $"'NOT contains(coll, (N)5)' returned {Py.Repr(exprN)}, must match legacy " +
            $"NOT_CONTAINS's {Py.Repr(legacyN)} over a population with no missing attribute");

        var legacyB = await LegacyNotContains(B("5"));
        Ensure(
            legacyB.SetEquals(Minus(all, "b", "bs")),
            $"legacy NOT_CONTAINS with a B operand excluded {Py.Repr(Minus(all, legacyB))}, expected " +
            "only the B (subsequence) and BS (member) items excluded");
        var exprB = await ExprNotContains(B("5"));
        Ensure(
            exprB.SetEquals(legacyB),
            $"'NOT contains(coll, (B)b5)' returned {Py.Repr(exprB)}, must match legacy " +
            $"NOT_CONTAINS's {Py.Repr(legacyB)} over a population with no missing attribute");
    }
}
