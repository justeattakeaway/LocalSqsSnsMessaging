// Translated from behavioral/dynamodb/check_projection_update_and_query_filter_expressions_accepted_at_4096_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-update-and-query-filter-expressions-accepted-at-4096-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionUpdateAndQueryFilterExpressionsAcceptedAt4096Bytes
{
    private const int Max = 4096;

    /// <summary>One attribute-name path of exactly <paramref name="total"/> bytes.</summary>
    private static string PathOfLen(int total) => "a" + new string('z', total - 1);

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (AmazonServiceException exc)
            {
                var code = exc.ErrorCode;
                if (notFound.Contains(code))
                {
                    return;
                }

                if (code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    private static readonly string[] NotFound = ["ResourceNotFoundException"];

    /// <summary>
    /// A 4096-byte ProjectionExpression (GetItem), UpdateExpression (UpdateItem) and Query
    /// FilterExpression are each accepted and applied, not just parsed.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-update-and-query-filter-expressions-accepted-at-4096-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-expr4096-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), NotFound));
        await WaitActive(ddb, ctx, table);

        // --- GetItem ProjectionExpression at exactly 4096 bytes ---
        const string projPrefix = "foo, ";
        var projPad = PathOfLen(Max - projPrefix.Length);
        var projection = projPrefix + projPad;
        Ensure(projection.Length == Max, $"ProjectionExpression length {projection.Length}, expected {Max}");

        await ddb.PutItemAsync(table, Item(
            ("pk", S("item")), ("sk", S("projection")),
            ("foo", S("kept")), ("bar", S("dropped")), (projPad, S("kept-too"))));
        var got = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item")), ("sk", S("projection"))),
            ProjectionExpression = projection,
            ConsistentRead = true,
        })).Item;
        Ensure(
            Py.Equal(got, Item(("foo", S("kept")), (projPad, S("kept-too")))),
            $"GetItem with a 4096-byte ProjectionExpression returned {Py.Repr(got)}, expected exactly 'foo' and the projected long attribute, excluding 'bar'");

        // --- UpdateItem UpdateExpression at exactly 4096 bytes ---
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("sk", S("update")), ("foo", S("before"))));
        const string updatePrefix = "SET foo = :v, ";
        const string updateSuffix = " = :v2";
        var updatePad = PathOfLen(Max - updatePrefix.Length - updateSuffix.Length);
        var updateExpr = updatePrefix + updatePad + updateSuffix;
        Ensure(updateExpr.Length == Max, $"UpdateExpression length {updateExpr.Length}, expected {Max}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item")), ("sk", S("update"))),
            UpdateExpression = updateExpr,
            ExpressionAttributeValues = Item((":v", S("after")), (":v2", S("added"))),
        });
        got = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item")), ("sk", S("update"))),
            ConsistentRead = true,
        })).Item;
        Ensure(
            Py.Equal(got, Item(("pk", S("item")), ("sk", S("update")), ("foo", S("after")), (updatePad, S("added")))),
            $"after a 4096-byte UpdateExpression the item read back {Py.Repr(got)}");

        // --- Query FilterExpression at exactly 4096 bytes ---
        await ddb.PutItemAsync(table, Item(("pk", S("query")), ("sk", S("k1")), ("flag", S("match"))));
        await ddb.PutItemAsync(table, Item(("pk", S("query")), ("sk", S("k2")), ("flag", S("other"))));
        const string filterPrefix = "flag = :v AND attribute_not_exists(";
        const string filterSuffix = ")";
        var filterPad = PathOfLen(Max - filterPrefix.Length - filterSuffix.Length);
        var filterExpr = filterPrefix + filterPad + filterSuffix;
        Ensure(filterExpr.Length == Max, $"FilterExpression length {filterExpr.Length}, expected {Max}");

        var page = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            FilterExpression = filterExpr,
            ExpressionAttributeValues = Item((":p", S("query")), (":v", S("match"))),
            ConsistentRead = true,
        });
        Ensure(
            (page.Count, page.ScannedCount) == (1, 2),
            $"Query with a 4096-byte FilterExpression gave Count {page.Count}, ScannedCount {page.ScannedCount}, expected 1 of 2 scanned");
        Ensure(
            Py.Equal(page.Items, new List<Dictionary<string, AttributeValue>> { Item(("pk", S("query")), ("sk", S("k1")), ("flag", S("match"))) }),
            $"Query with a 4096-byte FilterExpression returned {Py.Repr(page.Items)}");
    }
}
