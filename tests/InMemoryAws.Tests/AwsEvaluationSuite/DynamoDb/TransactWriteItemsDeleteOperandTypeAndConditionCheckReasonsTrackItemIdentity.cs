// Translated from behavioral/dynamodb/check_transact_write_items_delete_operand_type_and_condition_check_reasons_track_item_identity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-delete-operand-type-and-condition-check-reasons-track-item-identity</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsDeleteOperandTypeAndConditionCheckReasonsTrackItemIdentity
{
    private const string Check = "transact-write-items-delete-operand-type-and-condition-check-reasons-track-item-identity";
    private const string ErrCancelled = "TransactionCanceledException";
    private const string TypeMismatch = "An operand in the update expression has an incorrect data type";
    private const string CcfMessage = "The conditional request failed";

    /// <summary>
    /// A TransactWriteItems DELETE whose set operand has the wrong element type cancels with a
    /// per-item ValidationError reason, not a top-level error, and its reason tracks the item across
    /// a reordering of TransactItems exactly as a ConditionalCheckFailed reason beside it does.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-delete-operand-type-and-condition-check-reasons-track-item-identity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbtxn-");
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

        Dictionary<string, AttributeValue> TaggedKey() => Item(("pk", S("tagged")));
        Dictionary<string, AttributeValue> MissingKey() => Item(("pk", S("missing")));
        await ddb.PutItemAsync(t, Item(("pk", S("tagged")), ("tags", SS("red", "blue"))));
        // "missing" is never put, so its ConditionCheck fails every time below.

        TransactWriteItem DeleteWrongTypeAction() => new()
        {
            Update = new Update
            {
                TableName = t,
                Key = TaggedKey(),
                UpdateExpression = "DELETE tags :v",
                ExpressionAttributeValues = Item((":v", NS("1", "2"))),
            },
        };

        TransactWriteItem ConditionCheckAction() => new()
        {
            ConditionCheck = new ConditionCheck { TableName = t, Key = MissingKey(), ConditionExpression = "attribute_exists(pk)" },
        };

        var exc1 = await Cancelled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [DeleteWrongTypeAction(), ConditionCheckAction()],
        }));
        Ensure(exc1.Code == ErrCancelled, $"expected {ErrCancelled}, got {Py.Repr(exc1.Code)}");
        var reasons1 = exc1.CancellationReasons() ?? [];
        Ensure(reasons1.Count == 2, $"expected 2 positionally-aligned cancellation reasons, got {Py.Repr(reasons1.Select(Pair))}");
        Ensure(
            reasons1[0].Code == "ValidationError",
            $"expected the wrong-typed DELETE (index 0) to report ValidationError, got {Py.Repr(Pair(reasons1[0]))}");
        var validationMessage = reasons1[0].Message;
        Ensure(
            validationMessage == TypeMismatch,
            $"expected the ValidationError reason message {Py.Repr(TypeMismatch)}, got {Py.Repr(validationMessage)}");
        Ensure(
            reasons1[1].Code == "ConditionalCheckFailed",
            $"expected the failed ConditionCheck (index 1) to report ConditionalCheckFailed, got {Py.Repr(Pair(reasons1[1]))}");
        var ccfMessage = reasons1[1].Message;
        Ensure(
            ccfMessage == CcfMessage,
            $"expected the ConditionalCheckFailed reason message {Py.Repr(CcfMessage)}, got {Py.Repr(ccfMessage)}");
        const string expectedTopMessage1 = "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + "[ValidationError, ConditionalCheckFailed]";
        Ensure(
            exc1.Message == expectedTopMessage1,
            $"expected top-level message {Py.Repr(expectedTopMessage1)}, got {Py.Repr(exc1.Message)}");

        // Swap the two members' positions: the reason codes and messages follow their item, not
        // their slot.
        var exc2 = await Cancelled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [ConditionCheckAction(), DeleteWrongTypeAction()],
        }));
        Ensure(exc2.Code == ErrCancelled, $"expected {ErrCancelled}, got {Py.Repr(exc2.Code)}");
        var reasons2 = exc2.CancellationReasons() ?? [];
        Ensure(reasons2.Count == 2, $"expected 2 positionally-aligned cancellation reasons, got {Py.Repr(reasons2.Select(Pair))}");
        Ensure(
            reasons2[0].Code == "ConditionalCheckFailed",
            $"expected the ConditionCheck, now at index 0, to report ConditionalCheckFailed, got {Py.Repr(Pair(reasons2[0]))}");
        Ensure(
            reasons2[0].Message == ccfMessage,
            $"ConditionalCheckFailed message changed with position: {Py.Repr(reasons2[0].Message)} vs {Py.Repr(ccfMessage)}");
        Ensure(
            reasons2[1].Code == "ValidationError",
            $"expected the wrong-typed DELETE, now at index 1, to report ValidationError, got {Py.Repr(Pair(reasons2[1]))}");
        Ensure(
            reasons2[1].Message == validationMessage,
            $"ValidationError message changed with position: {Py.Repr(reasons2[1].Message)} vs {Py.Repr(validationMessage)}");
        const string expectedTopMessage2 = "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + "[ConditionalCheckFailed, ValidationError]";
        Ensure(
            exc2.Message == expectedTopMessage2,
            $"expected top-level message {Py.Repr(expectedTopMessage2)}, got {Py.Repr(exc2.Message)}");

        // Neither cancelled attempt touched the item under test.
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = TaggedKey(), ConsistentRead = true })).Item;
        Ensure(
            unchanged["tags"].SS.ToHashSet().SetEquals(["red", "blue"]),
            $"both cancelled transactions must leave tags untouched, got {Py.Repr(unchanged["tags"])}");

        // Positive control: fix both the operand type and the missing condition target, then the
        // identical shape of transaction commits.
        await ddb.PutItemAsync(t, MissingKey());
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = TaggedKey(),
                        UpdateExpression = "DELETE tags :v",
                        ExpressionAttributeValues = Item((":v", SS("red"))),
                    },
                },
                ConditionCheckAction(),
            ],
        });
        var shrunk = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = TaggedKey(), ConsistentRead = true })).Item;
        Ensure(
            shrunk["tags"].SS.ToHashSet().SetEquals(["blue"]),
            $"the positive-control DELETE must remove the named element, got {Py.Repr(shrunk["tags"])}");
    }

    private static (string Code, string Message) Pair(CancellationReason r) => (r.Code, r.Message);

    /// <summary>The error raised by <paramref name="fn"/>; an unexpected success raises instead.</summary>
    private static async Task<AwsError> Cancelled(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail($"expected {ErrCancelled}, but the call SUCCEEDED");
}
