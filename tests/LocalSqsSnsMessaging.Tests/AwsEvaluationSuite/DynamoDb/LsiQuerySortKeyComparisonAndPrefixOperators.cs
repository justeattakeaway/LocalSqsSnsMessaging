// Translated from behavioral/dynamodb/check_lsi_query_sort_key_comparison_and_prefix_operators.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-query-sort-key-comparison-and-prefix-operators</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiQuerySortKeyComparisonAndPrefixOperators
{
    private const string CheckId = "lsi-query-sort-key-comparison-and-prefix-operators";
    private static readonly string[] Eq = ["rev-2"];
    private static readonly string[] Lt = ["rev-0", "rev-1"];
    private static readonly string[] Le = ["rev-0", "rev-1", "rev-2"];
    private static readonly string[] Ge = ["rev-2", "rev-3", "rev-4", "rev-5", "rev-6"];
    private static readonly string[] Between = ["rev-1", "rev-2", "rev-3"];
    private static readonly string[] Begins = ["rev-5", "rev-6"];

    /// <summary>(sk, lsiSk) pairs under one partition; lsiSk values sort lexically as written.</summary>
    private static readonly (string Sk, string LsiSk)[] Rows =
    [
        ("rev-0", "d010"),
        ("rev-1", "d020"),
        ("rev-2", "d030"),
        ("rev-3", "d040"),
        ("rev-4", "d050"),
        ("rev-5", "e010"),
        ("rev-6", "e020"),
    ];

    private static async Task<List<string>> Query(IAmazonDynamoDB ddb, string table, string expr, Dictionary<string, AttributeValue> values)
    {
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi-ops",
            KeyConditionExpression = expr,
            ExpressionAttributeValues = values,
            ConsistentRead = true,
        });
        return [.. resp.Items.Select(item => item["sk"].S)];
    }

    /// <summary>
    /// A Query against an LSI accepts =, &lt;, &lt;=, &gt;=, BETWEEN and begins_with on the LSI sort
    /// key, each returning exactly the matching rows in ascending LSI sort key order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-query-sort-key-comparison-and-prefix-operators")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsiops-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsiSk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi-ops",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsiSk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (sk, lsiSk) in Rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("item-1")), ("sk", S(sk)), ("lsiSk", S(lsiSk))));
        }

        var eq = await Query(ddb, table, "pk = :pk AND lsiSk = :v", Item((":pk", S("item-1")), (":v", S("d030"))));
        Ensure(eq.SequenceEqual(Eq), $"lsiSk = 'd030' returned {Py.Repr(eq)}, expected ['rev-2']");

        var lt = await Query(ddb, table, "pk = :pk AND lsiSk < :v", Item((":pk", S("item-1")), (":v", S("d030"))));
        Ensure(lt.SequenceEqual(Lt), $"lsiSk < 'd030' returned {Py.Repr(lt)}, expected ['rev-0', 'rev-1']");

        var le = await Query(ddb, table, "pk = :pk AND lsiSk <= :v", Item((":pk", S("item-1")), (":v", S("d030"))));
        Ensure(le.SequenceEqual(Le), $"lsiSk <= 'd030' returned {Py.Repr(le)}, expected ['rev-0', 'rev-1', 'rev-2']");

        var ge = await Query(ddb, table, "pk = :pk AND lsiSk >= :v", Item((":pk", S("item-1")), (":v", S("d030"))));
        Ensure(ge.SequenceEqual(Ge), $"lsiSk >= 'd030' returned {Py.Repr(ge)}, expected ['rev-2', 'rev-3', 'rev-4', 'rev-5', 'rev-6']");

        var between = await Query(ddb, table, "pk = :pk AND lsiSk BETWEEN :lo AND :hi", Item((":pk", S("item-1")), (":lo", S("d020")), (":hi", S("d040"))));
        Ensure(between.SequenceEqual(Between), $"lsiSk BETWEEN 'd020' AND 'd040' returned {Py.Repr(between)}, expected ['rev-1', 'rev-2', 'rev-3']");

        var begins = await Query(ddb, table, "pk = :pk AND begins_with(lsiSk, :p)", Item((":pk", S("item-1")), (":p", S("e"))));
        Ensure(begins.SequenceEqual(Begins), $"begins_with(lsiSk, 'e') returned {Py.Repr(begins)}, expected ['rev-5', 'rev-6']");
    }
}
