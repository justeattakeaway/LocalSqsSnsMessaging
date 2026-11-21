// Translated from behavioral/dynamodb/check_scan_filter_contains_operand_type_matrix_legacy_and_expression.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-contains-operand-type-matrix-legacy-and-expression</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterContainsOperandTypeMatrixLegacyAndExpression
{
    private const string Check = "scan-filter-contains-operand-type-matrix-legacy-and-expression";

    private static SortedSet<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        new(items.Select(i => i["pk"].S), StringComparer.Ordinal);

    /// <summary>
    /// Legacy ScanFilter CONTAINS and FilterExpression contains() with an S, N or B operand each
    /// match only the identically typed scalar, the matching-type set's member, or an L element of
    /// that type, never a set or scalar of a different type holding the same digits.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-contains-operand-type-matrix-legacy-and-expression")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbcm-");
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
        await ddb.PutItemAsync(t, Item(("pk", S("l")), ("coll", L(S("5"), N("5"), B("5")))));

        async Task<SortedSet<string>> LegacyContains(AttributeValue value)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ConsistentRead = true,
                ScanFilter = new Dictionary<string, Condition>
                {
                    ["coll"] = new() { ComparisonOperator = ComparisonOperator.CONTAINS, AttributeValueList = [value] },
                },
            });
            return Ids(resp.Items);
        }

        async Task<SortedSet<string>> ExprContains(AttributeValue value)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = t,
                ConsistentRead = true,
                FilterExpression = "contains(coll, :v)",
                ExpressionAttributeValues = Item((":v", value)),
            });
            return Ids(resp.Items);
        }

        var legacyS = await LegacyContains(S("5"));
        Ensure(
            legacyS.SetEquals(["s", "ss", "l"]),
            $"legacy CONTAINS with an S operand matched {Py.Repr(legacyS)}, expected the S " +
            "(substring), SS (member) and L (element) items, never NS, B or BS holding the same digits");
        var exprS = await ExprContains(S("5"));
        Ensure(
            exprS.SetEquals(legacyS),
            $"contains(coll, (S)'5') matched {Py.Repr(exprS)}, must match legacy CONTAINS's " +
            $"{Py.Repr(legacyS)}");

        var legacyN = await LegacyContains(N("5"));
        Ensure(
            legacyN.SetEquals(["ns", "l"]),
            $"legacy CONTAINS with an N operand matched {Py.Repr(legacyN)}, expected only the NS " +
            "member and L element items, never S, SS, B or BS holding the same digits");
        var exprN = await ExprContains(N("5"));
        Ensure(
            exprN.SetEquals(legacyN),
            $"contains(coll, (N)5) matched {Py.Repr(exprN)}, must match legacy CONTAINS's " +
            $"{Py.Repr(legacyN)}");

        var legacyB = await LegacyContains(B("5"));
        Ensure(
            legacyB.SetEquals(["b", "bs", "l"]),
            $"legacy CONTAINS with a B operand matched {Py.Repr(legacyB)}, expected the B " +
            "(subsequence), BS (member) and L (element) items, never S, SS or NS holding the same digits");
        var exprB = await ExprContains(B("5"));
        Ensure(
            exprB.SetEquals(legacyB),
            $"contains(coll, (B)b'5') matched {Py.Repr(exprB)}, must match legacy CONTAINS's " +
            $"{Py.Repr(legacyB)}");
    }
}
