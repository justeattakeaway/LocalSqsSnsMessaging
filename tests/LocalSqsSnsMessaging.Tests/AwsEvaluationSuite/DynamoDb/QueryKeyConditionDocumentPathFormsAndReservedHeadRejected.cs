// Translated from behavioral/dynamodb/check_query_key_condition_document_path_forms_and_reserved_head_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-document-path-forms-and-reserved-head-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionDocumentPathFormsAndReservedHeadRejected
{
    private const string Nested = "Invalid KeyConditionExpression: KeyConditionExpressions cannot have conditions on nested attributes";
    private const string ReservedData = "Invalid KeyConditionExpression: Attribute name is a reserved keyword; reserved keyword: data";

    // (KeyConditionExpression, ExpressionAttributeValues, ExpressionAttributeNames)
    private static (string Kce, Dictionary<string, AttributeValue> Values, Dictionary<string, string>? Names)[] NestedForms() =>
    [
        ("pk.foo = :v", Item((":v", S("u"))), null),
        ("pk[0] = :v", Item((":v", S("u"))), null),
        ("pk = :p AND sk.foo = :v", Item((":p", S("u")), (":v", S("m"))), null),
        ("pk = :p AND sk[0] = :v", Item((":p", S("u")), (":v", S("m"))), null),
        ("pk = :p AND begins_with(sk.foo, :v)", Item((":p", S("u")), (":v", S("m"))), null),
        ("pk = :p AND sk.foo BETWEEN :v AND :w", Item((":p", S("u")), (":v", S("a")), (":w", S("z"))), null),
        ("pk = :p AND :v < sk.foo", Item((":p", S("u")), (":v", S("a"))), null),
        ("pk.foo > :v", Item((":v", S("u"))), null),
        ("foo.bar = :v", Item((":v", S("u"))), null),
        ("#d.foo = :v", Item((":v", S("u"))), Names(("#d", "pk"))),
    ];

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
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A key condition addressing an attribute through a document path (dot, list index, inside a
    /// function, BETWEEN or value-first) is refused as nested; a reserved word head is refused first.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Query.KeyConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-document-path-forms-and-reserved-head-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-kcenest-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);

        var item = Item(("pk", S("u")), ("sk", S("m")));
        await ddb.PutItemAsync(table, item);

        // Positive control: the top-level key paths select the item.
        var ok = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND sk = :v",
            ConsistentRead = true,
            ExpressionAttributeValues = Item((":p", S("u")), (":v", S("m"))),
        });
        Ensure(Py.Equal(ok.Items, new[] { item }), $"Query 'pk = :p AND sk = :v' returned {Py.Repr(ok.Items)}");

        foreach (var (kce, values, names) in NestedForms())
        {
            var request = new QueryRequest { TableName = table, KeyConditionExpression = kce, ExpressionAttributeValues = values };
            if (names is not null)
            {
                request.ExpressionAttributeNames = names;
            }

            var (code, msg) = await Error(() => ddb.QueryAsync(request));
            Ensure((code, msg) == ("ValidationException", Nested), $"KeyConditionExpression {Py.Repr(kce)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        {
            var (code, msg) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "data.foo = :v",
                ExpressionAttributeValues = Item((":v", S("u"))),
            }));
            Ensure((code, msg) == ("ValidationException", ReservedData), $"KeyConditionExpression 'data.foo = :v' gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var rows = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(Py.Equal(rows, new[] { item }), $"the table changed across the refused queries: {Py.Repr(rows)}");
    }
}
