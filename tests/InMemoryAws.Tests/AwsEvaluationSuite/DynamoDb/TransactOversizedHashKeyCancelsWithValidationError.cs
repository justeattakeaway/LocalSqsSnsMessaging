// Translated from behavioral/dynamodb/check_transact_oversized_hash_key_cancels_with_validation_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-oversized-hash-key-cancels-with-validation-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactOversizedHashKeyCancelsWithValidationError
{
    private const int HashMax = 2048;
    private const string CancelOneMsg = "Transaction cancelled, please refer cancellation reasons for specific reasons [ValidationError]";
    private const string CancelSecondMsg =
        "Transaction cancelled, please refer cancellation reasons for specific reasons [None, ValidationError]";

    private const string ReasonMsg =
        "One or more parameter values were invalid: Size of hashkey has exceeded the maximum size "
        + "limit of2048 bytes";

    /// <summary>
    /// A 2049-byte hash key in a TransactWriteItems Put, Delete, Update or ConditionCheck, or a
    /// TransactGetItems Get, cancels the transaction with a ValidationError reason; nothing is written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-oversized-hash-key-cancels-with-validation-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-txkeysize-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);

        // Positive control: an at-limit key commits through a transaction.
        var limitPk = new string('a', HashMax);
        var limitItem = Item(("pk", S(limitPk)), ("v", S("at-limit")));
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(limitPk)), ("v", S("at-limit"))) } }],
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(limitPk))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, limitItem), $"the at-limit transactional Put stored {Py.Repr(got)}");

        var bigPk = new string('a', HashMax + 1);
        Dictionary<string, AttributeValue> Big() => Item(("pk", S(bigPk)));
        var oneReason = new List<object?> { new Dictionary<string, object?> { ["Code"] = "ValidationError", ["Message"] = ReasonMsg } };
        var arms = new (string Label, Func<Task> Call)[]
        {
            ("TransactWriteItems Put", () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(bigPk)), ("v", S("x"))) } }],
            })),
            ("TransactWriteItems Delete", () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = [new TransactWriteItem { Delete = new Delete { TableName = table, Key = Big() } }],
            })),
            ("TransactWriteItems Update", () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Big(),
                            UpdateExpression = "SET v = :v",
                            ExpressionAttributeValues = Item((":v", S("x"))),
                        },
                    },
                ],
            })),
            ("TransactWriteItems ConditionCheck", () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck { TableName = table, Key = Big(), ConditionExpression = "attribute_not_exists(pk)" },
                    },
                ],
            })),
            ("TransactGetItems Get", () => ddb.TransactGetItemsAsync(new TransactGetItemsRequest
            {
                TransactItems = [new TransactGetItem { Get = new Get { TableName = table, Key = Big() } }],
            })),
        };
        foreach (var (label, call) in arms)
        {
            var (code, msg, reasons) = await Error(call);
            Ensure(
                (code, msg) == ("TransactionCanceledException", CancelOneMsg),
                $"{label} with a 2049-byte hash key gave {Py.Repr(code)}: {Py.Repr(msg)}");
            Ensure(Py.Equal(reasons, oneReason), $"{label} cancellation reasons were {Py.Repr(reasons)}");
        }

        // A valid sibling Put is not committed when the second action's key is over-size.
        {
            var (code, msg, reasons) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("sibling")), ("v", S("x"))) } },
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(bigPk)), ("v", S("x"))) } },
                ],
            }));
            Ensure(
                (code, msg) == ("TransactionCanceledException", CancelSecondMsg),
                $"a transaction whose second Put has a 2049-byte hash key gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var want = new List<object?>
            {
                new Dictionary<string, object?> { ["Code"] = "None" },
                new Dictionary<string, object?> { ["Code"] = "ValidationError", ["Message"] = ReasonMsg },
            };
            Ensure(Py.Equal(reasons, want), $"cancellation reasons were {Py.Repr(reasons)}, expected {Py.Repr(want)}");
        }

        var absent = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("sibling"))), ConsistentRead = true })).Item;
        Ensure(absent is null, $"the cancelled transaction still wrote its valid Put: {Py.Repr(absent)}");
        var count = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(count == 1, $"the table holds {count} items after the cancelled transactions, expected 1");
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

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>(Error.Code, Error.Message, CancellationReasons) of the error <paramref name="fn"/> raises.</summary>
    private static async Task<(string? Code, string? Message, List<object?>? Reasons)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail("expected the call to be refused, but it succeeded");
        return (error.Code, error.Message, error.CancellationReasons()?.Select(Reason).ToList());
    }

    /// <summary>A cancellation reason as boto3 hands it back.</summary>
    private static object? Reason(CancellationReason reason)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reason.Item is not null)
        {
            result["Item"] = reason.Item;
        }

        if (reason.Code is not null)
        {
            result["Code"] = reason.Code;
        }

        if (reason.Message is not null)
        {
            result["Message"] = reason.Message;
        }

        return result;
    }
}
