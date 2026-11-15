// Translated from behavioral/dynamodb/check_transact_condition_check_on_absent_item_passes_without_creating_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-condition-check-on-absent-item-passes-without-creating-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactConditionCheckOnAbsentItemPassesWithoutCreatingIt
{
    private const string CcfSecond =
        "Transaction cancelled, please refer cancellation reasons for specific reasons "
        + "[ConditionalCheckFailed, None]";

    private const string CcfReason = "The conditional request failed";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>Create an on-demand table keyed pk (S), plus sk of rangeType when given; wait ACTIVE.</summary>
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
    /// (Error.Code, Error.Message, CancellationReasons) of the ClientError fn() raises.
    /// An unexpected success fails, quoting the result.
    /// </summary>
    private static async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message, error.CancellationReasons());
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>The item at key read with ConsistentRead, or None.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    private static string ReasonsRepr(List<CancellationReason>? reasons) =>
        Py.Repr(reasons?.Select(r =>
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (r.Item is not null)
            {
                d["Item"] = r.Item;
            }

            d["Code"] = r.Code;
            if (r.Message is not null)
            {
                d["Message"] = r.Message;
            }

            return d;
        }).ToList());

    /// <summary>
    /// A ConditionCheck attribute_not_exists(pk) on a key with no item passes, its Put commits and the
    /// checked key stays absent; once that key exists the same transaction cancels and writes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-condition-check-on-absent-item-passes-without-creating-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txabs-");
        Dictionary<string, AttributeValue> Ghost() => Item(("pk", S("ghost")));

        Task<TransactWriteItemsResponse> Transact(string pk) => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck { TableName = table, Key = Ghost(), ConditionExpression = "attribute_not_exists(pk)" },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(pk)), ("v", S("ok"))) } },
            ],
        });

        await Transact("after-ghost");
        var got = await Get(ddb, table, Item(("pk", S("after-ghost"))));
        Ensure(Py.Equal(got, Item(("pk", S("after-ghost")), ("v", S("ok")))), $"the Put behind a passing check stored {Py.Repr(got)}");
        var @checked = await Get(ddb, table, Ghost());
        Ensure(@checked is null, $"a ConditionCheck on an absent key created {Py.Repr(@checked)}");

        await ddb.PutItemAsync(table, Item(("pk", S("ghost")), ("v", S("now-present"))));
        var (code, msg, reasons) = await Error(() => Transact("after-present"));
        Ensure(
            (code, msg) == ("TransactionCanceledException", CcfSecond),
            $"the check against a present item gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var gotReasons = (reasons ?? []).Select(r => (r.Code, (string?)r.Message)).ToList();
        Ensure(
            gotReasons.SequenceEqual([("ConditionalCheckFailed", CcfReason), ("None", (string?)null)]),
            $"cancellation reasons were {ReasonsRepr(reasons)}");
        Ensure(await Get(ddb, table, Item(("pk", S("after-present")))) is null, "a cancelled transaction wrote its Put");
    }
}
