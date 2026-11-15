// Translated from behavioral/dynamodb/check_projection_overlap_path_order_and_list_index_rendering_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-overlap-path-order-and-list-index-rendering-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionOverlapPathOrderAndListIndexRenderingRejected
{
    private static readonly string[] ResourceNotFound = ["ResourceNotFoundException"];

    private static string Overlap(string one, string two) =>
        $"Invalid ProjectionExpression: Two document paths overlap with each other; must remove or rewrite one of these paths; path one: {one}, path two: {two}";

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
    /// A ProjectionExpression overlap names path one/two in expression order (child first, the first
    /// overlapping pair after an unrelated path, a list index as [l, [0]]) on GetItem and Scan.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-overlap-path-order-and-list-index-rendering-rejected")]
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

        var item = Item(
            ("pk", S("k1")),
            ("x", S("vx")),
            ("a", M(("b", S("vb")), ("c", S("vc")))),
            ("alist", L(S("l0"), S("l1"))));
        await ddb.PutItemAsync(table, item);
        var key = Item(("pk", S("k1")));

        // Positive control: the same paths without an overlap are served.
        var ok = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true,
            ProjectionExpression = "x, a.b, alist[0]",
        })).Item;
        var want = Item(("x", S("vx")), ("a", M(("b", S("vb")))), ("alist", L(S("l0"))));
        Ensure(Py.Equal(ok, want), $"GetItem 'x, a.b, alist[0]' returned {Py.Repr(ok)}, expected {Py.Repr(want)}");

        var cases = new (string Expr, string Expected)[]
        {
            ("a.b, a", Overlap("[a, b]", "[a]")),
            ("x, a.b, a", Overlap("[a, b]", "[a]")),
            ("alist[0], alist", Overlap("[alist, [0]]", "[alist]")),
        };
        foreach (var (expr, expected) in cases)
        {
            var (code, msg) = await Error(async () => await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = key,
                ConsistentRead = true,
                ProjectionExpression = expr,
            }));
            Ensure(
                (code, msg) == ("ValidationException", expected),
                $"GetItem ProjectionExpression {Py.Repr(expr)} gave {Py.Repr(code)}: {Py.Repr(msg)}, expected {Py.Repr(expected)}");
        }

        var scanExpected = Overlap("[a]", "[a, b]");
        var (scanCode, scanMsg) = await Error(async () => await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ProjectionExpression = "a, a.b" }));
        Ensure(
            (scanCode, scanMsg) == ("ValidationException", scanExpected),
            $"Scan ProjectionExpression 'a, a.b' gave {Py.Repr(scanCode)}: {Py.Repr(scanMsg)}, expected {Py.Repr(scanExpected)}");

        var scanOk = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ProjectionExpression = "a.c" })).Items;
        Ensure(Py.Equal(scanOk, new[] { Item(("a", M(("c", S("vc"))))) }), $"Scan 'a.c' returned {Py.Repr(scanOk)}");

        var full = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(full, item), $"the item changed across the refused reads: {Py.Repr(full)}");
    }
}
