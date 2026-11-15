// Translated from behavioral/dynamodb/check_transact_write_items_put_string_set_with_zero_members_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-put-string-set-with-zero-members-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsPutStringSetWithZeroMembersRejected
{
    private const string Check = "transact-write-items-put-string-set-with-zero-members-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The AWS error code, message and error `fn` raises; an unexpected success fails loudly.</summary>
    private static async Task<(string? Code, string? Message, AwsError Response)> ErrorCode<T>(Func<Task<T>> fn, string what)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"{what}: the call was ACCEPTED with {result}; real AWS rejects it");
        }

        return (err.Code, err.Message, err);
    }

    /// <summary>
    /// A TransactWriteItems Put whose string set has zero members is a top-level
    /// ValidationException with no CancellationReasons, and a sibling Put in the same
    /// call does not land either.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-put-string-set-with-zero-members-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbtxss0-");
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

        // --- positive control: a sibling Put with a legal single-empty-string-member SS commits
        // on its own, so the rejection below is about the empty set, not about TransactWriteItems
        // refusing every SS it sees ---
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("control")), ("tags", SS(""))) } }],
        });
        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item ?? [];
        var controlTags = control.GetValueOrDefault("tags");
        Ensure(
            Py.Equal(controlTags, SS("")),
            $"positive control: a single-empty-string-member SS must commit and read back unchanged, got {Py.Repr(controlTags)}");

        // --- the probe: a zero-member SS beside a legal sibling Put, in one TransactWriteItems call ---
        const string expectedMessage = "One or more parameter values were invalid: An string set  may not be empty";
        var (code, message, resp) = await ErrorCode(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("sibling")), ("v", S("x"))) } },
                    new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("bad")), ("tags", SS())) } },
                ],
            }),
            "TransactWriteItems with a zero-member SS in one Put.Item beside a valid Put");
        Ensure(code == ErrValidation, $"a zero-member SS inside a TransactWriteItems Put must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == expectedMessage, $"expected AWS's exact message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        Ensure(
            resp.CancellationReasons() is null,
            $"a zero-member SS must be a top-level ValidationException with no CancellationReasons, got {resp}");

        var sibling = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("sibling"))), ConsistentRead = true })).Item;
        Ensure(
            sibling is null,
            $"the valid sibling Put must not land when the transaction is rejected top-level, found {Py.Repr(sibling)}");
        var bad = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("bad"))), ConsistentRead = true })).Item;
        Ensure(bad is null, $"the rejected zero-member SS Put must not land, found {Py.Repr(bad)}");

        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true, Select = Select.COUNT });
        Ensure(
            scanned.Count == 1,
            $"only the control item may exist after the rejected transaction, got {Py.Repr(scanned.Count)} items");
    }
}
