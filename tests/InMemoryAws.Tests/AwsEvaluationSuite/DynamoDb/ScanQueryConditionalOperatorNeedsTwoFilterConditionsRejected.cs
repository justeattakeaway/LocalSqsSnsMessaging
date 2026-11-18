// Translated from behavioral/dynamodb/check_scan_query_conditional_operator_needs_two_filter_conditions_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-query-conditional-operator-needs-two-filter-conditions-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanQueryConditionalOperatorNeedsTwoFilterConditionsRejected
{
    private const string WithoutFilterMsg = "ConditionalOperator cannot be used without Filter or Expected";
    private const string TwoOrMoreMsg = "ConditionalOperator can only be used when Filter or Expected has two or more elements";

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
    /// ConditionalOperator with an empty ScanFilter, only AttributesToGet, no key condition, or a
    /// one-condition ScanFilter/QueryFilter is ValidationException; a two-condition filter is accepted.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.ConditionalOperator.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-query-conditional-operator-needs-two-filter-conditions-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-condop-read-");
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
        (string Label, string Want, Func<Task<object>> Fn)[] cases =
        [
            ("Scan with ConditionalOperator and ScanFilter {}", WithoutFilterMsg,
                async () => await ddb.ScanAsync(new ScanRequest { TableName = table, ConditionalOperator = ConditionalOperator.AND, ScanFilter = [] })),
            ("Scan with ConditionalOperator and only AttributesToGet", WithoutFilterMsg,
                async () => await ddb.ScanAsync(new ScanRequest { TableName = table, ConditionalOperator = ConditionalOperator.AND, AttributesToGet = ["a", "b"] })),
            ("Scan with ConditionalOperator and a one-condition ScanFilter", TwoOrMoreMsg,
                async () => await ddb.ScanAsync(new ScanRequest { TableName = table, ConditionalOperator = ConditionalOperator.AND, ScanFilter = new() { ["a"] = Cond("x") } })),
            ("Query with ConditionalOperator and no key condition", WithoutFilterMsg,
                async () => await ddb.QueryAsync(new QueryRequest { TableName = table, ConditionalOperator = ConditionalOperator.AND })),
            ("Query with ConditionalOperator and a one-condition QueryFilter", TwoOrMoreMsg,
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    ConditionalOperator = ConditionalOperator.AND,
                    KeyConditions = new() { ["pk"] = Cond("k1") },
                    QueryFilter = new() { ["a"] = Cond("x") },
                })),
        ];
        foreach (var (label, want, fn) in cases)
        {
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var scan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConditionalOperator = ConditionalOperator.AND,
            ConsistentRead = true,
            ScanFilter = new() { ["a"] = Cond("x"), ["b"] = Cond("y") },
        });
        Ensure(Py.Equal(scan.Items, new[] { item }), $"Scan with a two-condition AND ScanFilter returned {Py.Repr(scan.Items)}");
        var query = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConditionalOperator = ConditionalOperator.OR,
            ConsistentRead = true,
            KeyConditions = new() { ["pk"] = Cond("k1") },
            QueryFilter = new() { ["a"] = Cond("x"), ["b"] = Cond("nope") },
        });
        Ensure(Py.Equal(query.Items, new[] { item }), $"Query with a two-condition OR QueryFilter returned {Py.Repr(query.Items)}");
    }
}
