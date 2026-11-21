// Translated from behavioral/dynamodb/check_transact_update_operand_type_mismatch_with_stored_value_cancels_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-update-operand-type-mismatch-with-stored-value-cancels-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactUpdateOperandTypeMismatchWithStoredValueCancelsRejected
{
    private const string Check = "transact-update-operand-type-mismatch-with-stored-value-cancels-rejected";
    private const string Canceled = "TransactionCanceledException";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";
    private const string WrongType = "An operand in the update expression has an incorrect data type";

    /// <summary>
    /// An Update member whose ADD, arithmetic or list_append meets a stored value of the wrong type
    /// cancels the transaction with a ValidationError reason, and no member is applied.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-update-operand-type-mismatch-with-stored-value-cancels-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 150);

        var stored = Item(("pk", S("doc")), ("s", S("text")), ("n", N("5")), ("h", L(S("a"))));
        await ddb.PutItemAsync(t, stored);

        TransactWriteItemsRequest Items(string updateExpression, Dictionary<string, AttributeValue> values) => new()
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("sibling")), ("v", N("1"))) } },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = Item(("pk", S("doc"))),
                        UpdateExpression = updateExpression,
                        ExpressionAttributeValues = values,
                    },
                },
            ],
        };

        Dictionary<string, AttributeValue> One() => Item((":one", N("1")));
        Dictionary<string, AttributeValue> Tail() => Item((":tail", L(S("b"))));
        // Each operand is well-typed on its own; only the stored value it meets is of the wrong type.
        foreach (var (expression, values) in new[]
        {
            ("ADD s :one", One()),
            ("SET s = s - :one", One()),
            ("SET n = list_append(n, :tail)", Tail()),
        })
        {
            var err = await Refusal(() => ddb.TransactWriteItemsAsync(Items(expression, values)));
            Ensure(err.Code == Canceled, $"{expression}: expected {Canceled}, got {err}");
            var expected = CanceledPrefix + "[None, ValidationError]";
            Ensure(err.Message == expected, $"{expression}: expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
            var reasons = Reasons(err);
            Ensure(
                Py.Equal(reasons, new[] { R("None"), R("ValidationError", WrongType) }),
                $"{expression}: cancellation reasons were {Py.Repr(reasons)}");
        }

        var sibling = await Get(ddb, t, "sibling");
        Ensure(sibling is null, $"a cancelled transaction wrote its sibling Put: {Py.Repr(sibling)}");
        var after = await Get(ddb, t, "doc");
        Ensure(Py.Equal(after, stored), $"a cancelled transaction changed the stored item: {Py.Repr(after)}");

        // Positive control: the same operators on attributes of the matching stored type commit.
        await ddb.TransactWriteItemsAsync(Items("ADD n :one SET h = list_append(h, :tail)", Item((":one", N("1")), (":tail", L(S("b"))))));
        sibling = await Get(ddb, t, "sibling");
        Ensure(Py.Equal(sibling, Item(("pk", S("sibling")), ("v", N("1")))), $"the committed sibling read {Py.Repr(sibling)}");
        var doc = await Get(ddb, t, "doc");
        Ensure(
            Py.Equal(doc, Item(("pk", S("doc")), ("s", S("text")), ("n", N("6")), ("h", L(S("a"), S("b"))))),
            $"the committed update read back as {Py.Repr(doc)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>The cancellation reasons as boto3 hands them back: each with only the members present.</summary>
    private static List<Dictionary<string, object?>>? Reasons(AwsError err) => err.CancellationReasons()?.Select(Reason).ToList();

    /// <summary>A cancellation reason as boto3 hands it back.</summary>
    private static Dictionary<string, object?> Reason(CancellationReason reason)
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

    /// <summary>A reason as the check writes it: <c>{'Code': code, 'Message': message}</c>, leaving out what is null.</summary>
    private static Dictionary<string, object?> R(string code, string? message = null, Dictionary<string, AttributeValue>? item = null)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (item is not null)
        {
            result["Item"] = item;
        }

        result["Code"] = code;
        if (message is not null)
        {
            result["Message"] = message;
        }

        return result;
    }

    /// <summary>The error <paramref name="fn"/> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<AwsError> Refusal(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error ?? throw Fail("expected the call to be refused, but it succeeded");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
