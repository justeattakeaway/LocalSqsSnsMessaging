// Translated from behavioral/dynamodb/check_update_item_malformed_or_overflowing_number_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-malformed-or-overflowing-number-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemMalformedOrOverflowingNumberRejected
{
    private const string NotNumericMsg = "1 validation error detected: The parameter cannot be converted to a numeric value: ";
    private const string OverflowMsg =
        "1 validation error detected: Number overflow. Attempting to store a number with magnitude " +
        "larger than supported range";
    private const string ArithmeticOverflowMsg =
        "Number overflow. Attempting to store a number with magnitude larger than supported range";
    private static readonly string MaxN = new string('9', 38) + "E+88";

    /// <summary>
    /// UpdateItem SET of N '12e', an NS holding 'abc' or a 201-digit N, AttributeUpdates N 'not_a_num',
    /// and SET +/-, ADD whose result exceeds the N range are ValidationException; item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-malformed-or-overflowing-number-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-upd-num-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        var key = Item(("pk", S("row1")));
        var item = Item(("pk", S("row1")), ("n", N("5")));
        await ddb.PutItemAsync(table, item);

        Task<UpdateItemResponse> SetBad(AttributeValue value) => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET bad = :v",
            ExpressionAttributeValues = Item((":v", value)),
        });

        var cases = new (string Label, string Want, Func<Task<UpdateItemResponse>> Fn)[]
        {
            ("SET bad = :v with N '12e'", NotNumericMsg + "12e", () => SetBad(N("12e"))),
            ("SET bad = :v with NS ['1', 'abc']", NotNumericMsg + "abc", () => SetBad(NS("1", "abc"))),
            ("SET bad = :v with a 201-digit N", OverflowMsg, () => SetBad(N("1" + new string('0', 200)))),
            ("AttributeUpdates PUT of N 'not_a_num'", NotNumericMsg + "not_a_num", () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
                {
                    ["bad"] = new AttributeValueUpdate { Value = N("not_a_num"), Action = AttributeAction.PUT },
                },
            })),
        };
        foreach (var (label, want, fn) in cases)
        {
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"UpdateItem {label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
            Ensure(Py.Equal(got, item), $"refused UpdateItem {label} changed the item to {Py.Repr(got)}");
        }

        var big = Item(("pk", S("big")), ("v", N(MaxN)));
        var neg = Item(("pk", S("neg")), ("v", N("-" + MaxN)));
        await ddb.PutItemAsync(table, big);
        await ddb.PutItemAsync(table, neg);
        var stored = new Dictionary<string, Dictionary<string, AttributeValue>>(StringComparer.Ordinal);
        foreach (var row in new[] { big, neg })
        {
            stored[row["pk"].S] = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", row["pk"])), ConsistentRead = true })).Item;
        }

        var arithmetic = new (string Label, Dictionary<string, AttributeValue> Row, string Expression)[]
        {
            ("SET v = v + :m on the largest N", big, "SET v = v + :m"),
            ("ADD v :m on the largest N", big, "ADD v :m"),
            ("SET v = v - :m on the most negative N", neg, "SET v = v - :m"),
        };
        foreach (var (label, row, expression) in arithmetic)
        {
            var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", row["pk"])),
                UpdateExpression = expression,
                ExpressionAttributeValues = Item((":m", N(MaxN))),
            }));
            Ensure((code, msg) == ("ValidationException", ArithmeticOverflowMsg), $"UpdateItem {label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", row["pk"])), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got, stored[row["pk"].S]), $"refused UpdateItem {label} changed the item to {Py.Repr(got)}");
        }

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET good = :v",
            ExpressionAttributeValues = Item((":v", N("42"))),
        });
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
            {
                ["legacy"] = new AttributeValueUpdate { Value = NS("1", "2"), Action = AttributeAction.PUT },
            },
        });
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(final.GetValueOrDefault("good"), N("42")), $"UpdateItem SET good = 42 left {Py.Repr(final)}");
        Ensure(
            (final.GetValueOrDefault("legacy")?.NS ?? []).Order(StringComparer.Ordinal).SequenceEqual(["1", "2"]),
            $"AttributeUpdates NS PUT left {Py.Repr(final)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = n + :one ADD c :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        });
        final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final.GetValueOrDefault("n"), N("6")) && Py.Equal(final.GetValueOrDefault("c"), N("1")),
            $"in-range arithmetic left {Py.Repr(final)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (Amazon.Runtime.AmazonServiceException exc)
            {
                if (exc.ErrorCode == notFound)
                {
                    return;
                }

                if (exc.ErrorCode != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var budget = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < budget)
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

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}")
            : (error.Code, error.Message);
    }
}
