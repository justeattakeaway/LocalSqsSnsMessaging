// Translated from behavioral/dynamodb/check_query_and_put_item_placeholder_and_reserved_word_validation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-and-put-item-placeholder-and-reserved-word-validation</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryAndPutItemPlaceholderAndReservedWordValidation
{
    private const string PutUnusedNameMsg =
        "1 validation error detected: Value provided in ExpressionAttributeNames unused in "
        + "expressions: keys: {#unused}";

    private const string QueryUndefinedFilterNameMsg =
        "Invalid FilterExpression: An expression attribute name used in the document path is not "
        + "defined; attribute name: #undefined";

    private const string QueryUnusedNameMsg = "Value provided in ExpressionAttributeNames unused in expressions: keys: {#unused}";
    private const string QueryUnusedValueMsg = "Value provided in ExpressionAttributeValues unused in expressions: keys: {:unused}";
    private const string QueryReservedFilterMsg = "Invalid FilterExpression: Attribute name is a reserved keyword; reserved keyword: name";

    private const string QueryUndefinedProjectionNameMsg =
        "Invalid ProjectionExpression: An expression attribute name used in the document path is not "
        + "defined; attribute name: #u";

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

    private static readonly string[] NotFound = ["ResourceNotFoundException"];

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus?.ToString() == "ACTIVE")
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
    /// Query refuses an undefined or unused placeholder, a bare reserved word and (before reading,
    /// even with no match) an undefined projection name; PutItem refuses an unused name.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-and-put-item-placeholder-and-reserved-word-validation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-qph-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), NotFound));
        await WaitActive(ddb, ctx, table);

        foreach (var (sk, n) in new[] { ("1", "5"), ("2", "50") })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S(sk)), ("n", N(n)), ("name", S($"n{sk}"))));
        }

        Dictionary<string, AttributeValue> PkValue(params (string Name, AttributeValue Value)[] extra) =>
            Item([(":pk", S("p")), .. extra]);

        // Positive control: the aliased form of every refused Query runs and filters.
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "#n > :v AND #nm = :nm",
            ExpressionAttributeNames = Names(("#n", "n"), ("#nm", "name")),
            ExpressionAttributeValues = PkValue((":v", N("10")), (":nm", S("n2"))),
            ConsistentRead = true,
        });
        var got = resp.Items.Select(i => i["sk"].S).ToList();
        Ensure(got.SequenceEqual(["2"]), $"the aliased control Query returned sort keys {Py.Repr(got)}, expected ['2']");

        var arms = new (string Label, string Want, Func<Task<object>> Call)[]
        {
            ("PutItem with an unused name", PutUnusedNameMsg, async () => await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("new")), ("sk", S("1"))),
                ConditionExpression = "attribute_not_exists(pk)",
                ExpressionAttributeNames = Names(("#unused", "zzz")),
            })),
            ("Query filter with an undefined name", QueryUndefinedFilterNameMsg, async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                FilterExpression = "#undefined > :v",
                ExpressionAttributeValues = PkValue((":v", N("10"))),
            })),
            ("Query with an unused name", QueryUnusedNameMsg, async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeNames = Names(("#unused", "zzz")),
                ExpressionAttributeValues = PkValue(),
            })),
            ("Query with an unused value", QueryUnusedValueMsg, async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = PkValue((":unused", S("x"))),
            })),
            ("Query filter with bare reserved word name", QueryReservedFilterMsg, async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                FilterExpression = "name = :v",
                ExpressionAttributeValues = PkValue((":v", S("n1"))),
            })),
            ("zero-match Query projecting a.#u undefined", QueryUndefinedProjectionNameMsg, async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                ProjectionExpression = "a.#u",
                ExpressionAttributeValues = Item((":pk", S("nobody"))),
            })),
        };
        foreach (var (label, want, call) in arms)
        {
            var (code, msg) = await Error(call);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var absent = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("new")), ("sk", S("1"))), ConsistentRead = true })).Item;
        Ensure(absent is null, $"the refused PutItem stored {Py.Repr(absent)}");
        var count = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(count == 2, $"the table holds {Py.Repr(count)} items after the refusals, expected 2");
    }
}
