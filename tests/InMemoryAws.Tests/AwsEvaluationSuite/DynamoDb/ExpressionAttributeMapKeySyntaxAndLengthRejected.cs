// Translated from behavioral/dynamodb/check_expression_attribute_map_key_syntax_and_length_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.expression-attribute-map-key-syntax-and-length-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExpressionAttributeMapKeySyntaxAndLengthRejected
{
    private const string EanTooLongPrefix =
        "ExpressionAttributeNames contains invalid key: The expression attribute map contains a key "
        + "that is too long; size of key: ";
    private const string EavTooLongMsg =
        "ExpressionAttributeValues contains invalid key: The expression attribute map contains a key "
        + "that is too long; size of key: 256";
    private const string EanEmptyValueMsg = "ExpressionAttributeNames contains invalid value: Empty attribute name for key #a";

    private static string EanSyntaxMsg(string key) => $"ExpressionAttributeNames contains invalid key: Syntax error; key: \"{key}\"";

    private static string EavSyntaxMsg(string key) => $"ExpressionAttributeValues contains invalid key: Syntax error; key: \"{key}\"";

    /// <summary>
    /// A placeholder key that is only its prefix, holds an invalid character or exceeds 255 bytes, or
    /// a name mapped to "", is ValidationException on GetItem and Scan; a valid key is accepted.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ExpressionAttributeNames.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.expression-attribute-map-key-syntax-and-length-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-eamap-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        static Dictionary<string, AttributeValue> Key() => Item(("pk", S("k1")));
        await ddb.PutItemAsync(table, Item(("pk", S("k1")), ("foo", S("a")), ("bar", S("b"))));

        Task<GetItemResponse> Get(string projection, Dictionary<string, string> names) => ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(),
            ProjectionExpression = projection,
            ExpressionAttributeNames = names,
            ConsistentRead = true,
        });

        Task<ScanResponse> Scan(string condition, Dictionary<string, AttributeValue> values) => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = condition,
            ExpressionAttributeValues = values,
        });

        var longName = "#" + new string('a', 255);
        var longBadName = "#" + new string('a', 255) + "-x";
        var longValue = ":" + new string('a', 255);
        (string Label, string Want, Func<Task<object>> Fn)[] cases =
        [
            ("GetItem with name key '#'", EanSyntaxMsg("#"), async () => await Get("#", Names(("#", "foo")))),
            ("GetItem with name key '#a-b'", EanSyntaxMsg("#a-b"), async () => await Get("#a", Names(("#a-b", "foo")))),
            ("GetItem with a 256-byte name key", EanTooLongPrefix + "256", async () => await Get("#a", Names((longName, "foo")))),
            ("GetItem with a 258-byte name key holding '-'", EanTooLongPrefix + "258", async () => await Get("#a", Names((longBadName, "foo")))),
            ("GetItem with name #a mapped to ''", EanEmptyValueMsg, async () => await Get("#a", Names(("#a", "")))),
            ("Scan with value key ':'", EavSyntaxMsg(":"), async () => await Scan("foo = :", Item((":", S("x"))))),
            ("Scan with value key ':a-b'", EavSyntaxMsg(":a-b"), async () => await Scan("foo = :v", Item((":a-b", S("x"))))),
            ("Scan with a 256-byte value key", EavTooLongMsg, async () => await Scan("foo = :v", Item((longValue, S("x"))))),
        ];
        foreach (var (label, want, fn) in cases)
        {
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var got = (await Get("#a_1", Names(("#a_1", "foo")))).Item;
        Ensure(Py.Equal(got, Item(("foo", S("a")))), $"GetItem projecting #a_1 -> foo returned {Py.Repr(got)}");
        var page = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "foo = :v_1",
            ExpressionAttributeValues = Item((":v_1", S("a"))),
            ConsistentRead = true,
        });
        Ensure(page.Count == 1, $"Scan filtering foo = :v_1 counted {Py.Repr(page.Count)}: {Py.Repr(page.Items)}");
    }

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
