// Translated from behavioral/dynamodb/check_transact_write_condition_function_type_mismatch_and_list_exact_match.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-condition-function-type-mismatch-and-list-exact-match</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteConditionFunctionTypeMismatchAndListExactMatch
{
    private const string Check = "transact-write-condition-function-type-mismatch-and-list-exact-match";
    private static readonly (string, string) CanceledOneFails = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed, None]");

    /// <summary>
    /// A TransactWriteItems ConditionCheck's begins_with against a Number attribute, and contains
    /// against a List for a value that is only a substring of one element, both evaluate false
    /// (cancelling the transaction with ConditionalCheckFailed) rather than raising a validation error.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-condition-function-type-mismatch-and-list-exact-match")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-twicf-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("number-item")), ("v", N("5")), ("sv", S("hello"))));
        await ddb.PutItemAsync(t, Item(("pk", S("list-item")), ("tags", L(S("hello"), S("world")))));

        Task<TransactWriteItemsResponse> GuardedTransaction((string Pk, string Expression) expr, AttributeValue value, string marker) =>
            ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = t,
                            Key = Item(("pk", S(expr.Pk))),
                            ConditionExpression = expr.Expression,
                            ExpressionAttributeValues = Item((":p", value)),
                        },
                    },
                    new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S(marker)), ("x", S("y"))) } },
                ],
            });

        // begins_with against the N attribute "v": a type mismatch evaluates false, cancelling the
        // transaction with ConditionalCheckFailed rather than ValidationException.
        var (code, message) = await Error(() => GuardedTransaction(("number-item", "begins_with(v, :p)"), S("5"), "marker-n-mismatch"));
        Ensure(
            (code, message) == CanceledOneFails,
            $"begins_with on an N attribute answered ({Py.Repr(code)}, {Py.Repr(message)}), expected {Py.Repr(CanceledOneFails)}");
        Ensure(!await Exists(ddb, t, "marker-n-mismatch"), "the cancelled transaction must not have written its companion Put");

        // Positive control: begins_with against the correctly typed S attribute "sv" still gates and
        // commits the companion Put, proving the mismatch above is about the type, not the function.
        await GuardedTransaction(("number-item", "begins_with(sv, :p)"), S("he"), "marker-n-control");
        Ensure(
            await Exists(ddb, t, "marker-n-control"),
            "begins_with on a correctly typed S attribute that holds must commit the companion Put");

        // contains against the List "tags": a value that is only a substring of one element ("ell" of
        // "hello") is not an element of the list, so the condition evaluates false and cancels.
        (code, message) = await Error(() => GuardedTransaction(("list-item", "contains(tags, :p)"), S("ell"), "marker-l-substring"));
        Ensure(
            (code, message) == CanceledOneFails,
            $"contains on a List with a substring-only value answered ({Py.Repr(code)}, {Py.Repr(message)}), "
            + $"expected {Py.Repr(CanceledOneFails)}");
        Ensure(!await Exists(ddb, t, "marker-l-substring"), "the cancelled transaction must not have written its companion Put");

        // Positive control: contains against the exact element "hello" commits the companion Put,
        // proving the list match requires an exact element, not a substring.
        await GuardedTransaction(("list-item", "contains(tags, :p)"), S("hello"), "marker-l-control");
        Ensure(
            await Exists(ddb, t, "marker-l-control"),
            "contains on a List matching an exact element must commit the companion Put");
    }

    private static async Task<bool> Exists(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item is not null;

    /// <summary>(code, message) of the error <paramref name="fn"/> raises; fails if it unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error is null ? throw Fail("expected the call to be refused, but it succeeded") : (error.Code, error.Message);
    }
}
