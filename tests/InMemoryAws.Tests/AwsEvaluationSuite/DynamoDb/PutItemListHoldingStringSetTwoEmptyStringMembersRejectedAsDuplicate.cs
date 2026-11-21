// Translated from behavioral/dynamodb/check_put_item_list_holding_string_set_two_empty_string_members_rejected_as_duplicate.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-list-holding-string-set-two-empty-string-members-rejected-as-duplicate</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemListHoldingStringSetTwoEmptyStringMembersRejectedAsDuplicate
{
    private const string Check = "put-item-list-holding-string-set-two-empty-string-members-rejected-as-duplicate";

    private const string ErrValidation = "ValidationException";

    private const string Invalid = "1 validation error detected: One or more parameter values were invalid: ";

    private static readonly (string Code, string Message) SsDuplicate = (ErrValidation, Invalid + "Input collection [\"\", \"\"] contains duplicates.");

    /// <summary>
    /// PutItem refuses an L element holding a string set with two empty-string members
    /// as a duplicate collection, and stores nothing; a legal two-member nested SS round-trips.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-list-holding-string-set-two-empty-string-members-rejected-as-duplicate")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-lssdup-");
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

        // Positive control: a legal two-member SS nested in an L round-trips.
        await ddb.PutItemAsync(t, Item(("pk", S("ok")), ("attr", L(SS("", "x")))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ok"))), ConsistentRead = true })).Item;
        Ensure(got is not null, "a legal two-member SS nested in an L must be stored");
        Ensure(
            got["attr"].L[0].SS.Order(StringComparer.Ordinal).SequenceEqual(["", "x"]),
            $"a legal two-member SS nested in an L must round-trip both members, got {Py.Repr(got["attr"])}");

        var item = Item(("pk", S("dup")), ("attr", L(SS("", ""))));
        await ExpectError(() => ddb.PutItemAsync(t, item), SsDuplicate);
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("dup"))), ConsistentRead = true })).Item is null,
            "a rejected duplicate-member SS nested in an L must not be stored");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }
}
