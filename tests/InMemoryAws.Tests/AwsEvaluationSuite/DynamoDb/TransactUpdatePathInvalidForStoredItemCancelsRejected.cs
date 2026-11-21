// Translated from behavioral/dynamodb/check_transact_update_path_invalid_for_stored_item_cancels_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-update-path-invalid-for-stored-item-cancels-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactUpdatePathInvalidForStoredItemCancelsRejected
{
    private const string Check = "transact-update-path-invalid-for-stored-item-cancels-rejected";
    private const string Canceled = "TransactionCanceledException";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";
    private const string InvalidPath = "The document path provided in the update expression is invalid for update";

    /// <summary>
    /// An Update member whose SET path descends through a stored scalar cancels the whole transaction
    /// with a ValidationError reason; ALL_OLD returns no Item for it, and the sibling Put is not applied.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-update-path-invalid-for-stored-item-cancels-rejected")]
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

        var stored = Item(("pk", S("doc")), ("m", M(("x", N("1")))), ("l", L(N("1"))));
        await ddb.PutItemAsync(t, stored);

        TransactWriteItemsRequest Items(string updateExpression) => new()
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
                        ExpressionAttributeValues = Item((":v", N("2"))),
                        ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                    },
                },
            ],
        };

        // m.x holds a number, so m.x.y names a map key inside a scalar.
        var err = await Refusal(() => ddb.TransactWriteItemsAsync(Items("SET m.x.y = :v")));
        Ensure(err.Code == Canceled, $"expected {Canceled}, got {err}");
        var expected = CanceledPrefix + "[None, ValidationError]";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var reasons = Reasons(err);
        Ensure(
            Py.Equal(reasons, new[] { R("None"), R("ValidationError", InvalidPath) }),
            $"cancellation reasons were {Py.Repr(reasons)}");

        // l is a list, so l.x names a map key inside a list: the same refusal.
        err = await Refusal(() => ddb.TransactWriteItemsAsync(Items("SET l.x = :v")));
        Ensure(err.Code == Canceled, $"expected {Canceled}, got {err}");
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)} for l.x, got {Py.Repr(err.Message)}");
        Ensure(
            Py.Equal(Reasons(err), new[] { R("None"), R("ValidationError", InvalidPath) }),
            $"cancellation reasons for l.x were {Py.Repr(Reasons(err))}");

        var sibling = await Get(ddb, t, "sibling");
        Ensure(sibling is null, $"the cancelled transaction wrote its sibling Put: {Py.Repr(sibling)}");
        var after = await Get(ddb, t, "doc");
        Ensure(Py.Equal(after, stored), $"the cancelled transaction changed the stored item: {Py.Repr(after)}");

        // Positive control: the same transaction with a path that fits the stored shape commits.
        await ddb.TransactWriteItemsAsync(Items("SET m.y = :v, l[0] = :v"));
        sibling = await Get(ddb, t, "sibling");
        Ensure(Py.Equal(sibling, Item(("pk", S("sibling")), ("v", N("1")))), $"the committed sibling read {Py.Repr(sibling)}");
        var doc = await Get(ddb, t, "doc");
        Ensure(
            Py.Equal(doc, Item(("pk", S("doc")), ("m", M(("x", N("1")), ("y", N("2")))), ("l", L(N("2"))))),
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
