// Translated from behavioral/dynamodb/check_filter_and_key_condition_expression_size_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.filter-and-key-condition-expression-size-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterAndKeyConditionExpressionSizeLimitRejected
{
    private const int Max = 4096;
    private const string FilterTooLongMsg = "Invalid FilterExpression: Expression size has exceeded the maximum allowed size;";
    private const string KeyConditionTooLongMsg = "Invalid KeyConditionExpression: Expression size has exceeded the maximum allowed size;";

    /// <summary>
    /// A FilterExpression (Scan, Query) or KeyConditionExpression over 4096 bytes is
    /// ValidationException; a 4096-byte FilterExpression is accepted.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.filter-and-key-condition-expression-size-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-expr-size-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        static Dictionary<string, AttributeValue> ItemValue() => Item(("pk", S("k1")), ("foo", S("a")));
        await ddb.PutItemAsync(table, ItemValue());
        const string suffix = " = :v";
        var overFilter = PathOfLen(Max + 1 - suffix.Length) + suffix;
        var atFilter = PathOfLen(Max - suffix.Length) + suffix;
        var overKey = "pk = :p AND " + PathOfLen(Max + 1 - "pk = :p AND  = :q".Length) + " = :q";
        Ensure(
            (overFilter.Length, atFilter.Length, overKey.Length) == (Max + 1, Max, Max + 1),
            $"expression lengths {overFilter.Length}, {atFilter.Length}, {overKey.Length}");
        static Dictionary<string, AttributeValue> V() => Item((":v", S("x")));
        static Dictionary<string, AttributeValue> P() => Item((":p", S("k1")));

        (string Label, string Want, Func<Task<object>> Fn)[] cases =
        [
            (
                "Scan with a 4097-byte FilterExpression",
                FilterTooLongMsg,
                async () => await ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = overFilter, ExpressionAttributeValues = V() })
            ),
            (
                "Query with a 4097-byte FilterExpression",
                FilterTooLongMsg,
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    KeyConditionExpression = "pk = :p",
                    FilterExpression = overFilter,
                    ExpressionAttributeValues = Item((":p", S("k1")), (":v", S("x"))),
                })
            ),
            (
                "Query with a 4097-byte KeyConditionExpression",
                KeyConditionTooLongMsg,
                async () => await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    KeyConditionExpression = overKey,
                    ExpressionAttributeValues = Item((":p", S("k1")), (":q", S("x"))),
                })
            ),
        ];
        foreach (var (label, want, fn) in cases)
        {
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var page = await ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = atFilter, ExpressionAttributeValues = V(), ConsistentRead = true });
        Ensure(
            (page.Count, page.ScannedCount) == (0, 1),
            $"Scan with a 4096-byte FilterExpression gave Count {Py.Repr(page.Count)}, ScannedCount {Py.Repr(page.ScannedCount)}");
        var queryPage = await ddb.QueryAsync(new QueryRequest { TableName = table, KeyConditionExpression = "pk = :p", ExpressionAttributeValues = P(), ConsistentRead = true });
        Ensure(Py.Equal(queryPage.Items, new[] { ItemValue() }), $"Query pk = :p returned {Py.Repr(queryPage.Items)}");
    }

    /// <summary>One attribute-name path of exactly `total` bytes.</summary>
    private static string PathOfLen(int total) => "a" + new string('z', total - 1);

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, ctx.TimeLeftSeconds - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < budget)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
