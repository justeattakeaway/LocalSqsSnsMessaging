// Translated from behavioral/dynamodb/check_projection_overlap_rejected_on_query_batch_and_transact_get.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-overlap-rejected-on-query-batch-and-transact-get</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionOverlapRejectedOnQueryBatchAndTransactGet
{
    private const string OverlapAAb =
        "Invalid ProjectionExpression: Two document paths overlap with each other; must remove or rewrite one of these paths; path one: [a], path two: [a, b]";

    private const string UnusedNameZ = "Value provided in ExpressionAttributeNames unused in expressions: keys: {#z}";

    private static readonly string[] ResourceNotFound = ["ResourceNotFoundException"];

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0)
    {
        return async () =>
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
    }

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription?> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var limit = Math.Max(0.0, ctx.TimeLeftSeconds - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < limit)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// ProjectionExpression 'a, a.b' is refused with the same overlap message on Query, BatchGetItem
    /// and TransactGetItems, and the overlap is reported ahead of an unused ExpressionAttributeNames key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-overlap-rejected-on-query-batch-and-transact-get")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-projovl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ResourceNotFound));
        await WaitActive(ddb, ctx, table);

        var item = Item(("pk", S("k1")), ("a", M(("b", S("vb")), ("c", S("vc")))), ("x", S("vx")));
        await ddb.PutItemAsync(table, item);
        var key = Item(("pk", S("k1")));
        var want = Item(("a", M(("b", S("vb")))), ("x", S("vx")));

        Task<QueryResponse> Query(string expr, Dictionary<string, string>? names = null)
        {
            var request = new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = Item((":p", S("k1"))),
                ConsistentRead = true,
                ProjectionExpression = expr,
            };
            if (names is not null)
            {
                request.ExpressionAttributeNames = names;
            }

            return ddb.QueryAsync(request);
        }

        Task<BatchGetItemResponse> BatchGet(string expr) => ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>
            {
                [table] = new KeysAndAttributes { Keys = [key], ProjectionExpression = expr, ConsistentRead = true },
            },
        });

        Task<TransactGetItemsResponse> TransactGet(string expr) => ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [new TransactGetItem { Get = new Get { TableName = table, Key = key, ProjectionExpression = expr } }],
        });

        // Positive controls: a non-overlapping projection is served by each operation.
        var got = (await Query("a.b, x")).Items;
        Ensure(Py.Equal(got, new[] { want }), $"Query 'a.b, x' returned {Py.Repr(got)}");
        got = (await BatchGet("a.b, x")).Responses.GetValueOrDefault(table);
        Ensure(Py.Equal(got, new[] { want }), $"BatchGetItem 'a.b, x' returned {Py.Repr(got)}");
        var transactGot = (await TransactGet("a.b, x")).Responses
            .Select(r => new Dictionary<string, object?> { ["Item"] = r.Item })
            .ToList();
        Ensure(
            Py.Equal(transactGot, new[] { new Dictionary<string, object?> { ["Item"] = want } }),
            $"TransactGetItems 'a.b, x' returned {Py.Repr(transactGot)}");

        var arms = new (string Label, Func<Task<object>> Fn)[]
        {
            ("Query", async () => await Query("a, a.b")),
            ("BatchGetItem", async () => await BatchGet("a, a.b")),
            ("TransactGetItems", async () => await TransactGet("a, a.b")),
        };
        foreach (var (label, fn) in arms)
        {
            var (armCode, armMsg) = await Error(fn);
            Ensure(
                (armCode, armMsg) == ("ValidationException", OverlapAAb),
                $"{label} ProjectionExpression 'a, a.b' gave {Py.Repr(armCode)}: {Py.Repr(armMsg)}");
        }

        // The unused name alone is refused, so the next arm decides precedence between two defects.
        var (code, msg) = await Error(async () => await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true,
            ProjectionExpression = "a.b, x",
            ExpressionAttributeNames = Names(("#z", "zzz")),
        }));
        Ensure(
            (code, msg) == ("ValidationException", UnusedNameZ),
            $"GetItem with only an unused name #z gave {Py.Repr(code)}: {Py.Repr(msg)}");

        (code, msg) = await Error(async () => await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true,
            ProjectionExpression = "a, a.b",
            ExpressionAttributeNames = Names(("#z", "zzz")),
        }));
        Ensure(
            (code, msg) == ("ValidationException", OverlapAAb),
            $"GetItem with an overlap and an unused name #z gave {Py.Repr(code)}: {Py.Repr(msg)}");
        (code, msg) = await Error(async () => await Query("a, a.b", Names(("#z", "zzz"))));
        Ensure(
            (code, msg) == ("ValidationException", OverlapAAb),
            $"Query with an overlap and an unused name #z gave {Py.Repr(code)}: {Py.Repr(msg)}");

        var full = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(full, item), $"the item changed across the refused reads: {Py.Repr(full)}");
    }
}
