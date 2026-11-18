// Translated from behavioral/dynamodb/check_query_filter_expression_never_and_partly_matching_costs_nothing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-never-and-partly-matching-costs-nothing</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionNeverAndPartlyMatchingCostsNothing
{
    private const string Check = "query-filter-expression-never-and-partly-matching-costs-nothing";

    private static readonly (string Sk, string Status)[] Rows =
    [
        ("o1", "pending"),
        ("o2", "complete"),
        ("o3", "pending"),
    ];

    /// <summary>
    /// A Query's ConsumedCapacity and ScannedCount do not depend on its FilterExpression: a
    /// never-matching and a partly-matching filter report the same of both as no filter at all, and
    /// only Count drops. With no filter, ScannedCount equals Count.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-never-and-partly-matching-costs-nothing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbqf-");
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
        foreach (var (sk, status) in Rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("u1")), ("sk", S(sk)), ("status", S(status))));
        }

        Task<QueryResponse> Query(string? filterExpression = null, Dictionary<string, string>? names = null, Dictionary<string, AttributeValue>? values = null)
        {
            var allValues = Item((":pk", S("u1")));
            foreach (var (k, v) in values ?? [])
            {
                allValues[k] = v;
            }

            return ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = allValues,
                ConsistentRead = true,
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
                FilterExpression = filterExpression,
                ExpressionAttributeNames = names,
            });
        }

        var unfiltered = await Query();
        Ensure(unfiltered.Count == Rows.Length, $"the unfiltered Query returned Count={Py.Repr(unfiltered.Count)}, expected {Py.Repr(Rows.Length)}");
        Ensure(
            unfiltered.ScannedCount == unfiltered.Count,
            $"with no FilterExpression, ScannedCount={Py.Repr(unfiltered.ScannedCount)} must equal Count={Py.Repr(unfiltered.Count)}");
        var baseCu = unfiltered.ConsumedCapacity.CapacityUnits;
        var baseScanned = unfiltered.ScannedCount;

        var never = await Query("#s = :never", Names(("#s", "status")), Item((":never", S("archived"))));
        Ensure(never.Count == 0, $"a FilterExpression matching no row must report Count=0, got {Py.Repr(never.Count)}");
        Ensure(
            never.ScannedCount == baseScanned,
            $"a never-matching filter changed ScannedCount to {Py.Repr(never.ScannedCount)}, expected the unfiltered {Py.Repr(baseScanned)}");
        var neverCu = never.ConsumedCapacity.CapacityUnits;
        Ensure(neverCu == baseCu, $"a never-matching filter changed ConsumedCapacity to {Py.Repr(neverCu)}, expected the unfiltered {Py.Repr(baseCu)}");

        var partly = await Query("#s = :p", Names(("#s", "status")), Item((":p", S("pending"))));
        var expectedPartial = Rows.Count(r => r.Status == "pending");
        Ensure(partly.Count == expectedPartial, $"a partly-matching filter returned Count={Py.Repr(partly.Count)}, expected {Py.Repr(expectedPartial)}");
        Ensure(
            partly.ScannedCount == baseScanned,
            $"a partly-matching filter changed ScannedCount to {Py.Repr(partly.ScannedCount)}, expected the unfiltered {Py.Repr(baseScanned)}");
        var partlyCu = partly.ConsumedCapacity.CapacityUnits;
        Ensure(partlyCu == baseCu, $"a partly-matching filter changed ConsumedCapacity to {Py.Repr(partlyCu)}, expected the unfiltered {Py.Repr(baseCu)}");

        var gotSks = partly.Items.Select(item => item["sk"].S).Order(StringComparer.Ordinal).ToList();
        var wantSks = Rows.Where(r => r.Status == "pending").Select(r => r.Sk).Order(StringComparer.Ordinal).ToList();
        Ensure(gotSks.SequenceEqual(wantSks), $"the partly-matching filter returned items {Py.Repr(gotSks)}, expected {Py.Repr(wantSks)}");
    }
}
