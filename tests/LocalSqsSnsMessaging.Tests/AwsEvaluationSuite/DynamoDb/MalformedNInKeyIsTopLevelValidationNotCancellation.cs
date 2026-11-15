// Translated from behavioral/dynamodb/check_malformed_n_in_key_is_top_level_validation_not_cancellation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.malformed-n-in-key-is-top-level-validation-not-cancellation</summary>
[Category("AwsEvaluationSuite")]
public sealed class MalformedNInKeyIsTopLevelValidationNotCancellation
{
    private const string CheckId = "malformed-n-in-key-is-top-level-validation-not-cancellation";
    private const string ErrValidation = "ValidationException";

    // Each form is malformed in a different way: unparsable mantissa, an empty string, and a
    // mantissa that parses but overflows the documented magnitude range.
    private static readonly string[] Malformed = ["abc", "", "1e99999"];

    private static string MessageFor(string mv) => mv == "1e99999"
        ? "Number overflow. Attempting to store a number with magnitude larger than supported range"
        : $"The parameter cannot be converted to a numeric value: {mv}";

    private static async Task<(string? Code, string? Message, AwsError Resp)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message, error);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// A malformed N in the key of GetItem, DeleteItem, Query, BatchGetItem, TransactGetItems
    /// and TransactWriteItems is a top-level ValidationException with no CancellationReasons, never
    /// a per-item cancellation, and nothing is written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html#HowItWorks.DataTypes.Number
    /// </summary>
    [Test]
    [DisplayName("dynamodb.malformed-n-in-key-is-top-level-validation-not-cancellation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        await ddb.PutItemAsync(table, Item(("pk", S("k1")), ("sk", N("5")), ("v", S("orig"))));
        Dictionary<string, AttributeValue> RealKey() => Item(("pk", S("k1")), ("sk", N("5")));

        async Task Unchanged()
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = RealKey(), ConsistentRead = true })).Item;
            Ensure(Py.Equal(item, Item(("pk", S("k1")), ("sk", N("5")), ("v", S("orig")))), $"item must be untouched by every rejected call, got {Py.Repr(item)}");
        }

        // --- positive control: GetItem on the real key succeeds ---
        var ok = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = RealKey(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(ok["v"], S("orig")), $"control GetItem must still succeed, got {Py.Repr(ok)}");

        foreach (var mv in Malformed)
        {
            var expected = MessageFor(mv);
            Dictionary<string, AttributeValue> BadKey() => Item(("pk", S("k1")), ("sk", N(mv)));

            var (code, message, _) = await Error(() => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = BadKey() }));
            Ensure(code == ErrValidation, $"GetItem key N {Py.Repr(mv)} must be {ErrValidation}, got {code}");
            Ensure(message == expected, $"expected AWS's exact GetItem message, got {Py.Repr(message)}");

            (code, message, _) = await Error(() => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = BadKey() }));
            Ensure(code == ErrValidation, $"DeleteItem key N {Py.Repr(mv)} must be {ErrValidation}, got {code}");
            Ensure(message == $"1 validation error detected: {expected}", $"expected AWS's exact DeleteItem message, got {Py.Repr(message)}");
            await Unchanged();

            (code, message, _) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk and sk = :sk",
                ExpressionAttributeValues = Item((":pk", S("k1")), (":sk", N(mv))),
            }));
            Ensure(code == ErrValidation, $"Query key condition N {Py.Repr(mv)} must be {ErrValidation}, got {code}");
            Ensure(message == expected, $"expected AWS's exact Query message, got {Py.Repr(message)}");

            (code, message, _) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal) { [table] = new KeysAndAttributes { Keys = [BadKey()] } },
            }));
            Ensure(code == ErrValidation, $"BatchGetItem key N {Py.Repr(mv)} must be {ErrValidation}, got {code}");
            Ensure(message == expected, $"expected AWS's exact BatchGetItem message, got {Py.Repr(message)}");

            AwsError resp;
            (code, message, resp) = await Error(() => ddb.TransactGetItemsAsync(new TransactGetItemsRequest
            {
                TransactItems = [new TransactGetItem { Get = new Get { TableName = table, Key = BadKey() } }],
            }));
            Ensure(code == ErrValidation, $"TransactGetItems key N {Py.Repr(mv)} must be {ErrValidation}, got {code}");
            Ensure(message == expected, $"expected AWS's exact TransactGetItems message, got {Py.Repr(message)}");
            Ensure(
                resp.CancellationReasons() is null,
                $"TransactGetItems with a malformed-N key must be a top-level ValidationException with no CancellationReasons, got {resp}");

            // The sibling Put in the same transaction must not land either -- this is a top-level
            // rejection of the whole request, not a per-item cancellation.
            Dictionary<string, AttributeValue> SiblingKey() => Item(("pk", S("k2")), ("sk", N("1")));
            (code, message, resp) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("k2")), ("sk", N("1")), ("v", S("new"))) } },
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = BadKey(),
                            UpdateExpression = "SET v = :v",
                            ExpressionAttributeValues = Item((":v", S("updated"))),
                        },
                    },
                ],
            }));
            Ensure(code == ErrValidation, $"TransactWriteItems key N {Py.Repr(mv)} must be {ErrValidation}, got {code}");
            Ensure(message == expected, $"expected AWS's exact TransactWriteItems message, got {Py.Repr(message)}");
            Ensure(
                resp.CancellationReasons() is null,
                $"TransactWriteItems with a malformed-N key must be a top-level ValidationException with no CancellationReasons, got {resp}");
            var sibling = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = SiblingKey(), ConsistentRead = true });
            Ensure(sibling.Item is null, $"the sibling Put must not land when the transaction is rejected top-level, found {Py.Repr(sibling.Item)}");

            await Unchanged();
        }

        // Control still works after every rejection.
        await ddb.DeleteItemAsync(table, RealKey());
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = RealKey(), ConsistentRead = true });
        Ensure(gone.Item is null, $"control DeleteItem must still succeed, found {Py.Repr(gone.Item)}");
    }
}
