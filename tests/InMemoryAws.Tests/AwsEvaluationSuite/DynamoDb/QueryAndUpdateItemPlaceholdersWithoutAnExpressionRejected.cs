// Translated from behavioral/dynamodb/check_query_and_update_item_placeholders_without_an_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-and-update-item-placeholders-without-an-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryAndUpdateItemPlaceholdersWithoutAnExpressionRejected
{
    private const string EanMsg = "ExpressionAttributeNames can only be specified when using expressions";
    private const string EavMsg = "ExpressionAttributeValues can only be specified when using expressions";
    private const string UpdateEanMsg = "1 validation error detected: " + EanMsg;
    private const string UpdateEavMsg = "1 validation error detected: " + EavMsg;
    private static readonly string[] NotFound = ["ResourceNotFoundException"];

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
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus?.ToString() == "ACTIVE"
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus?.ToString() == "ACTIVE"))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// ExpressionAttributeNames or Values on a legacy-KeyConditions Query, or on an UpdateItem with
    /// no UpdateExpression or ConditionExpression, is ValidationException and creates no item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-and-update-item-placeholders-without-an-expression-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-placeholders-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), NotFound));
        await WaitActive(ddb, ctx, table);
        var item = Item(("pk", S("k1")), ("foo", S("a")));
        await ddb.PutItemAsync(table, item);
        Dictionary<string, Condition> KeyConditions() => new(StringComparer.Ordinal)
        {
            ["pk"] = new Condition { AttributeValueList = [S("k1")], ComparisonOperator = ComparisonOperator.EQ },
        };
        var absent = Item(("pk", S("k2")));

        var cases = new (string Label, string Want, Func<Task<object>> Fn)[]
        {
            ("Query KeyConditions with ExpressionAttributeNames", EanMsg, async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditions = KeyConditions(),
                ExpressionAttributeNames = Names(("#a", "pk")),
            })),
            ("Query KeyConditions with ExpressionAttributeValues", EavMsg, async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditions = KeyConditions(),
                ExpressionAttributeValues = Item((":v", S("x"))),
            })),
            ("UpdateItem with only ExpressionAttributeNames", UpdateEanMsg, async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = absent,
                ExpressionAttributeNames = Names(("#a", "foo")),
            })),
            ("UpdateItem with only ExpressionAttributeValues", UpdateEavMsg, async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = absent,
                ExpressionAttributeValues = Item((":v", S("x"))),
            })),
        };
        foreach (var (label, want, fn) in cases)
        {
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var items = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(Py.Equal(items, new[] { item }), $"table after the refused calls holds {Py.Repr(items)}, expected only {Py.Repr(item)}");

        var page = await ddb.QueryAsync(new QueryRequest { TableName = table, KeyConditions = KeyConditions(), ConsistentRead = true });
        Ensure(Py.Equal(page.Items, new[] { item }), $"Query with KeyConditions alone returned {Py.Repr(page.Items)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = absent,
            UpdateExpression = "SET #a = :v",
            ExpressionAttributeNames = Names(("#a", "foo")),
            ExpressionAttributeValues = Item((":v", S("x"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = absent, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, Item(("pk", S("k2")), ("foo", S("x")))), $"UpdateItem SET #a = :v left {Py.Repr(got)}");
    }
}
