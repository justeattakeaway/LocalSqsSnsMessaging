// Translated from behavioral/dynamodb/check_batch_get_item_expression_and_legacy_parameter_rules.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-expression-and-legacy-parameter-rules</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemExpressionAndLegacyParameterRules
{
    private const string Mixed =
        "Can not use both expression and non-expression parameters in the same request: " +
        "Non-expression parameters: {AttributesToGet} Expression parameters: {ProjectionExpression}";

    private const string NamesWithoutExpression = "ExpressionAttributeNames can only be specified when using expressions";
    private const string UnusedName = "Value provided in ExpressionAttributeNames unused in expressions: keys: {#n}";

    /// <summary>
    /// BatchGetItem refuses ProjectionExpression with AttributesToGet on one table, names without a
    /// projection (also beside another table's AttributesToGet) and an unused name; legacy-only works.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_KeysAndAttributes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-expression-and-legacy-parameter-rules")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t1 = await CreateTable(ctx, ddb, "sim2real-behav-ddb-bgx-a-");
        var t2 = await CreateTable(ctx, ddb, "sim2real-behav-ddb-bgx-b-");
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("k1")));
        await ddb.PutItemAsync(t1, Item(("pk", S("k1")), ("a", S("A")), ("b", S("B"))));
        await ddb.PutItemAsync(t2, Item(("pk", S("k1")), ("c", S("C")), ("d", S("D"))));

        var refusals = new (string Label, Dictionary<string, KeysAndAttributes> Request, string Expected)[]
        {
            ("ProjectionExpression with AttributesToGet on one table",
                new(StringComparer.Ordinal) { [t1] = new KeysAndAttributes { Keys = [Key()], ProjectionExpression = "a", AttributesToGet = ["a"] } },
                Mixed),
            ("ExpressionAttributeNames without ProjectionExpression",
                new(StringComparer.Ordinal) { [t1] = new KeysAndAttributes { Keys = [Key()], ExpressionAttributeNames = Names(("#n", "a")) } },
                NamesWithoutExpression),
            ("names without a projection beside another table's AttributesToGet",
                new(StringComparer.Ordinal)
                {
                    [t1] = new KeysAndAttributes { Keys = [Key()], ExpressionAttributeNames = Names(("#n", "a")) },
                    [t2] = new KeysAndAttributes { Keys = [Key()], AttributesToGet = ["c"] },
                },
                NamesWithoutExpression),
            ("an ExpressionAttributeNames entry the projection does not use",
                new(StringComparer.Ordinal) { [t1] = new KeysAndAttributes { Keys = [Key()], ProjectionExpression = "pk", ExpressionAttributeNames = Names(("#n", "a")) } },
                UnusedName),
        };
        foreach (var (label, request, expected) in refusals)
        {
            var (code, msg) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = request }));
            Ensure((code, msg) == ("ValidationException", expected), $"BatchGetItem with {label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var legacy = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [t1] = new KeysAndAttributes { Keys = [Key()], AttributesToGet = ["a"], ConsistentRead = true },
                [t2] = new KeysAndAttributes { Keys = [Key()], AttributesToGet = ["c"], ConsistentRead = true },
            },
        });
        Ensure(
            Py.Equal(legacy.Responses, new Dictionary<string, List<Dictionary<string, AttributeValue>>>(StringComparer.Ordinal)
            {
                [t1] = [Item(("a", S("A")))],
                [t2] = [Item(("c", S("C")))],
            }),
            $"AttributesToGet on both tables returned {Py.Repr(legacy.Responses)}");
        var used = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [t1] = new KeysAndAttributes { Keys = [Key()], ProjectionExpression = "#n", ExpressionAttributeNames = Names(("#n", "b")), ConsistentRead = true },
            },
        });
        Ensure(
            Py.Equal(used.Responses, new Dictionary<string, List<Dictionary<string, AttributeValue>>>(StringComparer.Ordinal) { [t1] = [Item(("b", S("B")))] }),
            $"a projection using its only name returned {Py.Repr(used.Responses)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
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
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>Create an on-demand table keyed pk (S), plus sk of range_type when given; wait ACTIVE.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix, ScalarAttributeType? rangeType = null)
    {
        var table = FidelityContext.UniqueName(prefix);
        var keys = new List<KeySchemaElement> { new("pk", KeyType.HASH) };
        var attrs = new List<AttributeDefinition> { new("pk", ScalarAttributeType.S) };
        if (rangeType is not null)
        {
            keys.Add(new KeySchemaElement("sk", KeyType.RANGE));
            attrs.Add(new AttributeDefinition("sk", rangeType));
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = keys,
            AttributeDefinitions = attrs,
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError fn() raises.
    ///
    /// An unexpected success fails, quoting the result.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
