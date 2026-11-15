// Translated from behavioral/dynamodb/check_put_item_two_empty_members_in_ss_and_bs_rejected_as_duplicate_collections.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-two-empty-members-in-ss-and-bs-rejected-as-duplicate-collections</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemTwoEmptyMembersInSsAndBsRejectedAsDuplicateCollections
{
    private const string CheckId = "put-item-two-empty-members-in-ss-and-bs-rejected-as-duplicate-collections";
    private const string ErrValidation = "ValidationException";

    /// <summary>The AWS error code and message <paramref name="fn"/> raises; an unexpected success fails the check.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task<PutItemResponse>> fn, string what)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it, got {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Python's sorted() over set members as boto3 returns them (str or bytes).</summary>
    private static List<object> SortedMembers(AttributeValue? value, string kind)
    {
        if (kind == "SS")
        {
            return (value?.SS ?? []).Order(StringComparer.Ordinal).Cast<object>().ToList();
        }

        var bytes = (value?.BS ?? []).Select(b => b.ToArray()).ToList();
        bytes.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        return bytes.Cast<object>().ToList();
    }

    /// <summary>
    /// A two-member SS or BS whose members are both the empty string/bytes is refused
    /// ValidationException as a duplicate collection, not as an empty one, and nothing is stored.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-two-empty-members-in-ss-and-bs-rejected-as-duplicate-collections")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-emptydup-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // --- positive controls: a single empty-string/empty-bytes member is a legal, non-duplicate
        // member of a two-element set, so these round-trip. ---
        await ddb.PutItemAsync(t, Item(("pk", S("ok-ss")), ("s", SS("", "a"))));
        await ddb.PutItemAsync(t, Item(("pk", S("ok-bs")), ("s", BS([], Encoding.ASCII.GetBytes("a")))));
        var controls = new List<(string Key, string Kind, List<object> Want)>
        {
            ("ok-ss", "SS", ["", "a"]),
            ("ok-bs", "BS", [Array.Empty<byte>(), Encoding.ASCII.GetBytes("a")]),
        };
        foreach (var (key, kind, want) in controls)
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true })).Item ?? [];
            var members = SortedMembers(got.GetValueOrDefault("s"), kind);
            Ensure(
                Py.Equal(members, want),
                $"control: a legal two-member {kind} with one empty member must round-trip as {Py.Repr(want)}, got {Py.Repr(got.GetValueOrDefault("s"))}");
        }

        // --- the rejections: two empty members of the same type are duplicates of each other ---
        const string invalid = "1 validation error detected: One or more parameter values were invalid: ";
        const string expectedSsMessage = invalid + "Input collection [\"\", \"\"] contains duplicates.";
        const string expectedBsMessage = invalid + "Input collection [\"\",\"\"]of type BS contains duplicates.";

        var (code, message) = await ErrorCode(
            () => ddb.PutItemAsync(t, Item(("pk", S("ss-two-empty")), ("s", SS("", "")))),
            "PutItem with an SS of two empty-string members");
        Ensure(
            code == ErrValidation,
            $"an SS of two empty-string members must be refused {ErrValidation} as a duplicate collection, got {Py.Repr(code)}");
        Ensure(message == expectedSsMessage, $"expected the message {Py.Repr(expectedSsMessage)}, got {Py.Repr(message)}");

        (code, message) = await ErrorCode(
            () => ddb.PutItemAsync(t, Item(("pk", S("bs-two-empty")), ("s", BS([], [])))),
            "PutItem with a BS of two empty-bytes members");
        Ensure(
            code == ErrValidation,
            $"a BS of two empty-bytes members must be refused {ErrValidation} as a duplicate collection, got {Py.Repr(code)}");
        Ensure(message == expectedBsMessage, $"expected the message {Py.Repr(expectedBsMessage)}, got {Py.Repr(message)}");

        // A silently de-duplicated write would return the same 200, so read back to prove neither stored.
        foreach (var key in new[] { "ss-two-empty", "bs-two-empty" })
        {
            var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true });
            Ensure(got.Item is null, $"a write refused as a duplicate collection must leave nothing behind, got {Py.Repr(got.Item)}");
        }
    }
}
