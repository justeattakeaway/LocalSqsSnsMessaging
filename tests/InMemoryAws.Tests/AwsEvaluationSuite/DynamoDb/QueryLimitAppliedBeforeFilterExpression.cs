// Translated from behavioral/dynamodb/check_query_limit_applied_before_filter_expression.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-limit-applied-before-filter-expression</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryLimitAppliedBeforeFilterExpression
{
    /// <summary>
    /// Limit bounds the items examined, not returned: a Limit=2 Query whose only filter match is the fifth item returns
    /// Count 0, ScannedCount 2 and LastEvaluatedKey sk 002 on page one, and the match only on the third page.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-limit-applied-before-filter-expression")]
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
            Tags = [new Tag { Key = "sim2real", Value = "query-limit-before-filter" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // Only sk '005' matches the filter; it sorts LAST in the partition.
        for (var i = 1; i < 6; i++)
        {
            await ddb.PutItemAsync(t, Item(
                ("pk", S("u1")),
                ("sk", S(i.ToString("D3", CultureInfo.InvariantCulture))),
                ("state", S(i == 5 ? "match" : "other"))));
        }

        QueryRequest Filtered(int? limit = null, Dictionary<string, AttributeValue>? exclusiveStartKey = null) => new()
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "#st = :m",
            ExpressionAttributeNames = Names(("#st", "state")),
            ExpressionAttributeValues = Item((":p", S("u1")), (":m", S("match"))),
            ConsistentRead = true,
            Limit = limit,
            ExclusiveStartKey = exclusiveStartKey,
        };

        var first = await ddb.QueryAsync(Filtered(limit: 2));
        Ensure(
            first.Count == 0,
            $"Limit=2 examines sk 001+002, neither of which matches the filter, so Count must be 0; got Count={first.Count} Items={Py.Repr(first.Items)}");
        Ensure(first.Items is null or { Count: 0 }, $"Count=0 page must carry no Items, got {Py.Repr(first.Items)}");
        Ensure(
            first.ScannedCount == 2,
            $"Limit=2 must be charged against items EXAMINED, so ScannedCount must be 2; got {first.ScannedCount}");
        Ensure(
            first.LastEvaluatedKey is not null,
            "a Limit-truncated Query must return a LastEvaluatedKey even when Count=0, otherwise the caller cannot reach the remaining items");
        Ensure(
            Py.Equal(first.LastEvaluatedKey, Item(("pk", S("u1")), ("sk", S("002")))),
            $"LastEvaluatedKey must be the last EXAMINED key (sk 002), not the last matched key; got {Py.Repr(first.LastEvaluatedKey)}");

        // Page forward: the match is only found on the third page.
        var pages = 1;
        var scannedTotal = first.ScannedCount ?? 0;
        var matched = new List<string>();
        var resp = first;
        while (resp.LastEvaluatedKey is not null)
        {
            Ensure(pages < 6, "pagination did not terminate");
            resp = await ddb.QueryAsync(Filtered(limit: 2, exclusiveStartKey: resp.LastEvaluatedKey));
            pages++;
            scannedTotal += resp.ScannedCount ?? 0;
            matched.AddRange(resp.Items.Select(it => it["sk"].S));
        }

        Ensure(pages == 3, $"5 items at Limit=2 must take exactly 3 pages, got {pages}");
        Ensure(scannedTotal == 5, $"total ScannedCount across pages must be 5, got {scannedTotal}");
        Ensure(matched.SequenceEqual(["005"]), $"exactly sk 005 must be returned across the pages, got {Py.Repr(matched)}");

        // Control: the same filtered Query with NO Limit returns the match in one page, with
        // ScannedCount reflecting the whole partition and no LastEvaluatedKey.
        var unlimited = await ddb.QueryAsync(Filtered());
        Ensure(unlimited.Count == 1, $"unlimited filtered Query must return 1 item, got {unlimited.Count}");
        Ensure(unlimited.ScannedCount == 5, $"unlimited filtered Query must report ScannedCount=5, got {unlimited.ScannedCount}");
        Ensure(unlimited.LastEvaluatedKey is null, $"an exhausted Query must omit LastEvaluatedKey, got {Py.Repr(unlimited.LastEvaluatedKey)}");
        Ensure(Py.Equal(unlimited.Items[0]["sk"], S("005")), Py.Repr(unlimited.Items));
    }
}
