// Translated from behavioral/dynamodb/check_delete_item_key_duplicate_set_member_byte_order_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-key-duplicate-set-member-byte-order-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemKeyDuplicateSetMemberByteOrderRejected
{
    private const string Check = "delete-item-key-duplicate-set-member-byte-order-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    /// <summary>
    /// A duplicate member in a DeleteItem Key's BS is refused ValidationException naming the
    /// distinct members seen so far in ascending byte order before the duplicate, with the
    /// duplicate appended; the same refusal for NS names no members at all.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-key-duplicate-set-member-byte-order-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-delkey-");
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

        await ddb.PutItemAsync(t, Item(("pk", S("control")), ("note", S("kept"))));
        await ddb.PutItemAsync(t, Item(("pk", S("victim")), ("note", S("to-delete"))));

        // positive control: an ordinary DeleteItem with a scalar key succeeds and removes exactly it
        await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = t, Key = Item(("pk", S("victim"))) });
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("victim"))), ConsistentRead = true });
        Ensure(got.Item is null, $"DeleteItem with a valid scalar key must remove the item, got {Py.Repr(got.Item)}");

        const string invalid = "1 validation error detected: One or more parameter values were invalid: ";
        var bsDup = (ErrValidation, invalid + "Input collection [\"AQ==\",\"eg==\",\"AQ==\"]of type BS contains duplicates.");
        var nsDup = (ErrValidation, invalid + "Input collection contains duplicates.");

        // a DeleteItem Key whose BS has a duplicate: the two distinct members seen before the
        // duplicate (0x7a, 0x01) are named in ascending byte order (0x01 then 0x7a), and the
        // duplicate (0x01) is appended last -- not the order the request gave them in.
        await ExpectError(
            async () => await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = t, Key = Item(("pk", BS([0x7a], [0x01], [0x01]))) }),
            bsDup);

        // the same shape on NS: AWS refuses it the same way but names no members at all.
        await ExpectError(
            async () => await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = t, Key = Item(("pk", NS("50", "3", "7", "3"))) }),
            nsDup);

        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(control, Item(("pk", S("control")), ("note", S("kept")))),
            $"the control item must be untouched by the rejected DeleteItem calls, got {Py.Repr(control)}");
        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true, Select = Select.COUNT });
        Ensure(
            scanned.Count == 1,
            $"only the control item may remain after the victim's delete and the two rejected DeleteItem calls, got {scanned.Count} items");
    }
}
