// Translated from behavioral/dynamodb/check_query_value_first_sort_key_comparison_and_size_of_absent_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-value-first-sort-key-comparison-and-size-of-absent-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryValueFirstSortKeyComparisonAndSizeOfAbsentAttribute
{
    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    private static List<int> Sks(QueryResponse resp) => [.. resp.Items.Select(it => int.Parse(it["sk"].N, CultureInfo.InvariantCulture))];

    /// <summary>
    /// A key condition with the value on the left (':lo &lt;= sk', ':hi > sk') selects what the
    /// sort-key-first form does; size() of an absent attribute matches nothing, size('') matches 0.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Query.KeyConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-value-first-sort-key-comparison-and-size-of-absent-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-qcmp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        for (var i = 1; i < 11; i++)
        {
            var item = Item(("pk", S("u")), ("sk", N(i.ToString(CultureInfo.InvariantCulture))), ("tag", S(i == 4 ? "" : $"t{i}")));
            await ddb.PutItemAsync(table, item);
        }

        Task<QueryResponse> Query(string kce, Dictionary<string, AttributeValue> values, string? filterExpression = null)
        {
            var allValues = Item((":p", S("u")));
            foreach (var (k, v) in values)
            {
                allValues[k] = v;
            }

            return ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = kce,
                ConsistentRead = true,
                ExpressionAttributeValues = allValues,
                FilterExpression = filterExpression,
            });
        }

        var lo = Item((":lo", N("3")));
        var valueFirst = await Query("pk = :p AND :lo <= sk", lo);
        var keyFirst = await Query("pk = :p AND sk >= :lo", lo);
        Ensure(Sks(valueFirst).SequenceEqual(Enumerable.Range(3, 8)), $"':lo <= sk' with lo=3 selected {Py.Repr(Sks(valueFirst))}");
        Ensure(
            Sks(valueFirst).SequenceEqual(Sks(keyFirst)),
            $"':lo <= sk' {Py.Repr(Sks(valueFirst))} differs from 'sk >= :lo' {Py.Repr(Sks(keyFirst))}");
        var hi = await Query("pk = :p AND :hi > sk", Item((":hi", N("4"))));
        Ensure(Sks(hi).SequenceEqual([1, 2, 3]), $"':hi > sk' with hi=4 selected {Py.Repr(Sks(hi))}");
        var zero = Item((":zero", N("0")));
        var absent = await Query("pk = :p", zero, filterExpression: "size(nonexistent_attr) = :zero");
        Ensure(
            (absent.Count, absent.ScannedCount) == (0, 10),
            $"size(nonexistent_attr) = 0 must match nothing of 10 items, got {Py.Repr(new Dictionary<string, object?> { ["Items"] = absent.Items, ["Count"] = absent.Count, ["ScannedCount"] = absent.ScannedCount })}");
        var empty = await Query("pk = :p", zero, filterExpression: "size(tag) = :zero");
        Ensure(Sks(empty).SequenceEqual([4]), $"size(tag) = 0 must match only the empty-string item 4, got {Py.Repr(Sks(empty))}");
    }
}
