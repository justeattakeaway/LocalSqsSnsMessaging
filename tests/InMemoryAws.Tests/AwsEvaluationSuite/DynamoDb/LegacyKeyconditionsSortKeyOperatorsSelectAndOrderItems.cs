// Translated from behavioral/dynamodb/check_legacy_keyconditions_sort_key_operators_select_and_order_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-keyconditions-sort-key-operators-select-and-order-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyKeyconditionsSortKeyOperatorsSelectAndOrderItems
{
    private const string CheckId = "legacy-keyconditions-sort-key-operators-select-and-order-items";

    // Ascending byte order of the sort-key values this check seeds: alpha < beta < delta < epsilon < gamma.
    private static readonly string[] SortKeys = ["alpha", "beta", "delta", "epsilon", "gamma"];
    private static readonly string[] GammaEpsilon = ["gamma", "epsilon"];

    // (ComparisonOperator, AttributeValueList, KeyConditionExpression fragment, expected sk's ascending)
    private static (string Op, Func<List<AttributeValue>> Values, string ExprFragment, string[] Expected)[] OperatorCases() =>
    [
        ("EQ", () => [S("delta")], "sk = :v0", ["delta"]),
        ("LE", () => [S("delta")], "sk <= :v0", ["alpha", "beta", "delta"]),
        ("LT", () => [S("delta")], "sk < :v0", ["alpha", "beta"]),
        ("GE", () => [S("delta")], "sk >= :v0", ["delta", "epsilon", "gamma"]),
        ("GT", () => [S("delta")], "sk > :v0", ["epsilon", "gamma"]),
        ("BEGINS_WITH", () => [S("de")], "begins_with(sk, :v0)", ["delta"]),
        ("BETWEEN", () => [S("beta"), S("epsilon")], "sk BETWEEN :v0 AND :v1", ["beta", "delta", "epsilon"]),
    ];

    private static List<string> Sks(QueryResponse resp) => [.. resp.Items.Select(item => item["sk"].S)];

    /// <summary>
    /// A legacy KeyConditions sort-key operator (EQ/LE/LT/GE/GT/BEGINS_WITH/BETWEEN) selects and
    /// orders the same items as its KeyConditionExpression twin; ScanIndexForward=false reverses the
    /// order, and a hash-only condition returns the whole partition.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.KeyConditions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-keyconditions-sort-key-operators-select-and-order-items")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        foreach (var sk in SortKeys)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S(sk))));
        }

        Dictionary<string, Condition> PkCond() => new(StringComparer.Ordinal)
        {
            ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ },
        };

        foreach (var (op, values, exprFragment, expected) in OperatorCases())
        {
            var conditions = PkCond();
            conditions["sk"] = new Condition { AttributeValueList = values(), ComparisonOperator = op };
            var legacy = await ddb.QueryAsync(new QueryRequest { TableName = t, KeyConditions = conditions, ConsistentRead = true });
            var exprValues = Item((":p", S("p1")));
            var valueList = values();
            for (var i = 0; i < valueList.Count; i++)
            {
                exprValues[$":v{i}"] = valueList[i];
            }

            var expr = await ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                KeyConditionExpression = $"pk = :p AND {exprFragment}",
                ExpressionAttributeValues = exprValues,
                ConsistentRead = true,
            });

            Ensure(Sks(legacy).SequenceEqual(expected), $"{op}: expected {Py.Repr(expected)}, got {Py.Repr(Sks(legacy))}");
            Ensure(legacy.Count == expected.Length, $"{op}: expected Count={expected.Length}, got {Py.Repr(legacy.Count)}");
            Ensure(Sks(expr).SequenceEqual(expected), $"{op} expression twin: expected {Py.Repr(expected)}, got {Py.Repr(Sks(expr))}");
            Ensure(expr.Count == legacy.Count, $"{op}: legacy Count {Py.Repr(legacy.Count)} != expression twin Count {Py.Repr(expr.Count)}");
        }

        var whole = await ddb.QueryAsync(new QueryRequest { TableName = t, KeyConditions = PkCond(), ConsistentRead = true });
        Ensure(Sks(whole).SequenceEqual(SortKeys), $"hash-only: expected the whole partition {Py.Repr(SortKeys)}, got {Py.Repr(Sks(whole))}");
        Ensure(whole.Count == 5, $"hash-only: expected Count=5, got {Py.Repr(whole.Count)}");

        var reversedWhole = await ddb.QueryAsync(new QueryRequest { TableName = t, KeyConditions = PkCond(), ScanIndexForward = false, ConsistentRead = true });
        var wantReversed = SortKeys.Reverse().ToList();
        Ensure(Sks(reversedWhole).SequenceEqual(wantReversed), $"ScanIndexForward=False: expected {Py.Repr(wantReversed)}, got {Py.Repr(Sks(reversedWhole))}");

        var gtConditions = PkCond();
        gtConditions["sk"] = new Condition { AttributeValueList = [S("delta")], ComparisonOperator = ComparisonOperator.GT };
        var reversedGt = await ddb.QueryAsync(new QueryRequest { TableName = t, KeyConditions = gtConditions, ScanIndexForward = false, ConsistentRead = true });
        Ensure(Sks(reversedGt).SequenceEqual(GammaEpsilon), $"GT delta reversed: expected ['gamma', 'epsilon'], got {Py.Repr(Sks(reversedGt))}");
    }
}
