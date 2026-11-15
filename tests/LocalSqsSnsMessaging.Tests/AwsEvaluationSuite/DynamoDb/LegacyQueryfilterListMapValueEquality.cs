// Translated from behavioral/dynamodb/check_legacy_queryfilter_list_map_value_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-queryfilter-list-map-value-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyQueryfilterListMapValueEquality
{
    private const string CheckId = "legacy-queryfilter-list-map-value-equality";

    private static AttributeValue ListValue() => L(S("x"), N("1"));

    private static AttributeValue ListOther() => L(S("x"), N("2"));

    private static AttributeValue MapValue() => M(("a", S("y")), ("b", N("1")));

    private static AttributeValue MapOther() => M(("a", S("y")), ("b", N("2")));

    /// <summary>
    /// Legacy QueryFilter ComparisonOperator EQ on a List or Map AttributeValue narrows a
    /// partition by deep structural equality, scanning every item under the key condition first.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-queryfilter-list-map-value-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-qfdoc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        var rows = new (string Sk, AttributeValue Doc)[]
        {
            ("s0", ListValue()),
            ("s1", ListOther()),
            ("s2", MapValue()),
            ("s3", MapOther()),
        };
        foreach (var (sk, doc) in rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S(sk)), ("doc", doc)));
        }

        Task<QueryResponse> QueryDocEq(AttributeValue refValue) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditions = new Dictionary<string, Condition>(StringComparer.Ordinal)
            {
                ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ },
            },
            QueryFilter = new Dictionary<string, Condition>(StringComparer.Ordinal)
            {
                ["doc"] = new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [refValue] },
            },
            ConsistentRead = true,
        });

        var listResp = await QueryDocEq(ListValue());
        Ensure(
            listResp.Count == 1,
            $"legacy QueryFilter EQ on an L value must narrow the 4-item partition to the one structurally identical List, got Count={Py.Repr(listResp.Count)}");
        Ensure(Py.Equal(listResp.Items[0]["sk"], S("s0")), $"expected the surviving item to be sk=s0, got {Py.Repr(listResp.Items[0].GetValueOrDefault("sk"))}");
        Ensure(
            listResp.ScannedCount == 4,
            $"legacy QueryFilter is a post-condition filter -- all 4 items under pk=p1 must be scanned before the doc filter narrows it to 1, got ScannedCount={Py.Repr(listResp.ScannedCount)}");

        var mapResp = await QueryDocEq(MapValue());
        Ensure(
            mapResp.Count == 1,
            $"legacy QueryFilter EQ on an M value must narrow the 4-item partition to the one structurally identical Map, got Count={Py.Repr(mapResp.Count)}");
        Ensure(Py.Equal(mapResp.Items[0]["sk"], S("s2")), $"expected the surviving item to be sk=s2, got {Py.Repr(mapResp.Items[0].GetValueOrDefault("sk"))}");
        Ensure(
            mapResp.ScannedCount == 4,
            $"legacy QueryFilter is a post-condition filter -- all 4 items under pk=p1 must be scanned before the doc filter narrows it to 1, got ScannedCount={Py.Repr(mapResp.ScannedCount)}");
    }
}
