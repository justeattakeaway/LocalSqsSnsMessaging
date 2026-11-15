// Translated from behavioral/dynamodb/check_batch_request_shape_defects_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-request-shape-defects-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchRequestShapeDefectsRejected
{
    private const string EmptyRequestItems = "The requestItems parameter is required for BatchWriteItem";
    private const string BatchGetEmptyRequestItems =
        "1 validation error detected: Value at 'RequestItems' failed to satisfy constraint: " +
        "Member must have length greater than or equal to 1";
    private const string KeySchema = "The provided key element does not match the schema";

    /// <summary>
    /// An empty RequestItems map, and a BatchGetItem or BatchWriteItem DeleteRequest key that is
    /// wrong-typed, missing its sort key or carries an extra attribute, are ValidationExceptions.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchGetItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-request-shape-defects-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-bshape-", rangeType: ScalarAttributeType.N);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("seed")), ("sk", N("1")));
        var seed = Item(("pk", S("seed")), ("sk", N("1")), ("v", S("kept")));
        await ddb.PutItemAsync(table, seed);

        foreach (var (label, want, call) in new (string, string, Func<Task>)[]
        {
            ("BatchGetItem", BatchGetEmptyRequestItems, () => ddb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = [] })),
            ("BatchWriteItem", EmptyRequestItems, () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = [] })),
        })
        {
            var (code, msg) = await Error(call);
            Ensure((code, msg) == ("ValidationException", want), $"{label} with an empty RequestItems map gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var badKeys = new (string Label, Dictionary<string, AttributeValue> Bad)[]
        {
            ("a number-typed hash key", Item(("pk", N("1")), ("sk", N("1")))),
            ("a missing sort key", Item(("pk", S("seed")))),
            ("an extra non-key attribute", Item(("pk", S("seed")), ("sk", N("1")), ("v", S("kept")))),
        };
        foreach (var (label, bad) in badKeys)
        {
            var (code, msg) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new() { [table] = new KeysAndAttributes { Keys = [Key(), bad] } },
            }));
            Ensure((code, msg) == ("ValidationException", KeySchema), $"BatchGetItem with {label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var extra = Item(("pk", S("seed")), ("sk", N("1")), ("v", S("kept")));
        var (code2, msg2) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] =
                [
                    new WriteRequest { DeleteRequest = new DeleteRequest { Key = Item(("pk", S("other")), ("sk", N("2"))) } },
                    new WriteRequest { DeleteRequest = new DeleteRequest { Key = extra } },
                ],
            },
        }));
        Ensure(
            (code2, msg2) == ("ValidationException", KeySchema),
            $"BatchWriteItem DeleteRequest with an extra key attribute gave {Py.Repr(code2)}: {Py.Repr(msg2)}");
        var got = await Get(ddb, table, Key());
        Ensure(Py.Equal(got, seed), $"the refused batch delete changed the seed to {Py.Repr(got)}");

        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new() { [table] = new KeysAndAttributes { Keys = [Key()], ConsistentRead = true } },
        });
        Ensure(
            Py.Equal(resp.Responses, new Dictionary<string, List<Dictionary<string, AttributeValue>>>(StringComparer.Ordinal) { [table] = [seed] }),
            $"a well-keyed BatchGetItem returned {Py.Repr(resp.Responses)}");
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { DeleteRequest = new DeleteRequest { Key = Key() } }] },
        });
        Ensure(await Get(ddb, table, Key()) is null, "a well-keyed batch DeleteRequest left the seed in place");
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
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("expected the call to be refused, but it succeeded");
    }

    /// <summary>The item at key read with ConsistentRead, or None.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
