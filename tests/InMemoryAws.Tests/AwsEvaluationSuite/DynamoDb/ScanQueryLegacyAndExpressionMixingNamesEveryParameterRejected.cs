// Translated from behavioral/dynamodb/check_scan_query_legacy_and_expression_mixing_names_every_parameter_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-query-legacy-and-expression-mixing-names-every-parameter-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanQueryLegacyAndExpressionMixingNamesEveryParameterRejected
{
    private static string MixingMsg(string legacy, string expr) =>
        "Can not use both expression and non-expression parameters in the same request: " +
        $"Non-expression parameters: {{{legacy}}} Expression parameters: {{{expr}}}";

    /// <summary>A legacy EQ condition on one string value.</summary>
    private static Condition Cond(string value) => new() { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S(value)] };

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
    /// Scan or Query mixing legacy and expression parameters is ValidationException listing every
    /// one present per side in AWS's order, ahead of an unused value; KeyConditions + filter is fine.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-query-legacy-and-expression-mixing-names-every-parameter-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-mixing-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        var item = Item(("pk", S("k1")), ("a", S("x")), ("b", S("y")));
        await ddb.PutItemAsync(table, item);
        Dictionary<string, AttributeValue> V() => Item((":v", S("x")));
        Dictionary<string, AttributeValue> P() => Item((":p", S("k1")));
        Dictionary<string, AttributeValue> PV() => Item((":p", S("k1")), (":v", S("x")));
        (string Legacy, string Expr, string Label, Func<Task<object>> Fn)[] cases =
        [
            ("ConditionalOperator", "ProjectionExpression", "Scan ConditionalOperator + ProjectionExpression",
                async () => await ddb.ScanAsync(new ScanRequest { TableName = table, ConditionalOperator = ConditionalOperator.AND, ProjectionExpression = "a" })),
            ("ConditionalOperator", "KeyConditionExpression", "Query ConditionalOperator + KeyConditionExpression",
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    ConditionalOperator = ConditionalOperator.AND,
                    KeyConditionExpression = "pk = :p",
                    ExpressionAttributeValues = P(),
                })),
            ("AttributesToGet, ScanFilter, ConditionalOperator", "ProjectionExpression, FilterExpression", "Scan with every legacy and expression parameter",
                async () => await ddb.ScanAsync(new ScanRequest
                {
                    TableName = table,
                    AttributesToGet = ["a"],
                    ScanFilter = new() { ["a"] = Cond("x") },
                    ConditionalOperator = ConditionalOperator.AND,
                    FilterExpression = "a = :v",
                    ProjectionExpression = "b",
                    ExpressionAttributeValues = V(),
                })),
            ("QueryFilter, ConditionalOperator, KeyConditions", "ProjectionExpression, FilterExpression, KeyConditionExpression",
                "Query with every legacy and expression parameter",
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    KeyConditions = new() { ["pk"] = Cond("k1") },
                    QueryFilter = new() { ["a"] = Cond("x") },
                    ConditionalOperator = ConditionalOperator.AND,
                    KeyConditionExpression = "pk = :p",
                    FilterExpression = "a = :v",
                    ProjectionExpression = "b",
                    ExpressionAttributeValues = PV(),
                })),
            ("ConditionalOperator", "FilterExpression", "Scan ConditionalOperator + FilterExpression with an unused value",
                async () => await ddb.ScanAsync(new ScanRequest
                {
                    TableName = table,
                    ConditionalOperator = ConditionalOperator.AND,
                    FilterExpression = "a = :v",
                    ExpressionAttributeValues = Item((":v", S("x")), (":unused", S("y"))),
                })),
            ("ScanFilter", "ProjectionExpression", "Scan ScanFilter + ProjectionExpression",
                async () => await ddb.ScanAsync(new ScanRequest { TableName = table, ScanFilter = new() { ["a"] = Cond("x") }, ProjectionExpression = "b" })),
            ("AttributesToGet", "KeyConditionExpression", "Query AttributesToGet + KeyConditionExpression",
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    AttributesToGet = ["a"],
                    KeyConditionExpression = "pk = :p",
                    ExpressionAttributeValues = P(),
                })),
            ("QueryFilter", "FilterExpression, KeyConditionExpression", "Query QueryFilter + FilterExpression + KeyConditionExpression",
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    QueryFilter = new() { ["a"] = Cond("x") },
                    KeyConditionExpression = "pk = :p",
                    FilterExpression = "a = :v",
                    ExpressionAttributeValues = PV(),
                })),
            ("QueryFilter", "FilterExpression", "Query KeyConditions + QueryFilter + FilterExpression",
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    KeyConditions = new() { ["pk"] = Cond("k1") },
                    QueryFilter = new() { ["a"] = Cond("x") },
                    FilterExpression = "a = :v",
                    ExpressionAttributeValues = V(),
                })),
        ];
        foreach (var (legacy, expr, label, fn) in cases)
        {
            var want = MixingMsg(legacy, expr);
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var query = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditions = new() { ["pk"] = Cond("k1") },
            FilterExpression = "a = :v",
            ExpressionAttributeValues = V(),
            ConsistentRead = true,
        });
        Ensure(Py.Equal(query.Items, new[] { item }), $"Query KeyConditions + FilterExpression returned {Py.Repr(query.Items)}");
        query = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditions = new() { ["pk"] = Cond("k1") },
            ProjectionExpression = "a",
            ConsistentRead = true,
        });
        Ensure(Py.Equal(query.Items, new[] { Item(("a", S("x"))) }), $"Query KeyConditions + ProjectionExpression returned {Py.Repr(query.Items)}");
    }
}
