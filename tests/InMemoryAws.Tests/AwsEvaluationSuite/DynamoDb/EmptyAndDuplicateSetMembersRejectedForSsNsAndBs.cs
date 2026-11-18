// Translated from behavioral/dynamodb/check_empty_and_duplicate_set_members_rejected_for_ss_ns_and_bs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.empty-and-duplicate-set-members-rejected-for-ss-ns-and-bs</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyAndDuplicateSetMembersRejectedForSsNsAndBs
{
    private const string CheckId = "empty-and-duplicate-set-members-rejected-for-ss-ns-and-bs";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// Empty SS/NS/BS sets and duplicate members (incl. NS '1' vs '1.0', and duplicates inside a
    /// ConditionExpression value or a GetItem key) are refused ValidationException with AWS's exact
    /// messages and store/read nothing; legal sets and an SS with an empty-string member round-trip.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.empty-and-duplicate-set-members-rejected-for-ss-ns-and-bs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-sets-");
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

        // --- positive controls: one legal two-member set per type, on the same table ---
        (string Key, string Kind, AttributeValue Value)[] controls =
        [
            ("ok-ss", "SS", SS("a", "b")),
            ("ok-ns", "NS", NS("1", "2")),
            ("ok-bs", "BS", BS("a"u8.ToArray(), "b"u8.ToArray())),
        ];
        foreach (var (key, _, value) in controls)
        {
            await ddb.PutItemAsync(t, Item(("pk", S(key)), ("s", value)));
        }

        foreach (var (key, kind, _) in controls)
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true })).Item ?? [];
            var s = got.GetValueOrDefault("s");
            var members = s is null ? null : (Py.Boto(s).GetValueOrDefault(kind) as System.Collections.IList);
            Ensure(members is not null, $"control: a legal two-member {kind} must round-trip as a {kind}, got {Py.Repr(s)}");
            Ensure(members.Count == 2, $"control: a legal two-member {kind} must keep both members, got {Py.Repr(members)}");
        }

        // --- the rejections: empty and duplicate, for each of the three set types ---
        const string invalid = "1 validation error detected: One or more parameter values were invalid: ";
        (string Key, string Why, Dictionary<string, AttributeValue> Item, string ExpectedMessage)[] rejections =
        [
            ("ss-empty", "an empty string set", Item(("pk", S("ss-empty")), ("s", SS())), invalid + "An string set  may not be empty"),
            ("ss-dup", "a string set with a repeated member", Item(("pk", S("ss-dup")), ("s", SS("a", "a"))), invalid + "Input collection [\"a\", \"a\"] contains duplicates."),
            ("ns-empty", "an empty number set", Item(("pk", S("ns-empty")), ("s", NS())), invalid + "An number set  may not be empty"),
            ("ns-dup", "a number set with a repeated member", Item(("pk", S("ns-dup")), ("s", NS("1", "1"))), invalid + "Input collection contains duplicates."),
            (
                "ns-dup-canonical",
                "a number set whose members ('1' and '1.0') are equal once canonicalized",
                Item(("pk", S("ns-dup-canonical")), ("s", NS("1", "1.0"))),
                invalid + "Input collection contains duplicates."
            ),
            ("bs-empty", "an empty binary set", Item(("pk", S("bs-empty")), ("s", BS())), invalid + "Binary sets should not be empty"),
            (
                "bs-dup",
                "a binary set with a repeated member",
                Item(("pk", S("bs-dup")), ("s", BS("a"u8.ToArray(), "a"u8.ToArray()))),
                invalid + "Input collection [\"YQ==\",\"YQ==\"]of type BS contains duplicates."
            ),
            (
                "ss-dup-in-m",
                "a string set with a repeated member nested inside an M",
                Item(("pk", S("ss-dup-in-m")), ("m", M(("s", SS("a", "a"))))),
                invalid + "Input collection [\"a\", \"a\"] contains duplicates."
            ),
        ];
        foreach (var (_, why, item, expectedMessage) in rejections)
        {
            var (code, message) = await ErrorCode(() => ddb.PutItemAsync(t, item), $"PutItem with {why}");
            Ensure(code == ErrValidation, $"{why} is not a legal set and must be refused {ErrValidation}, got {Py.Repr(code)}");
            Ensure(message == expectedMessage, $"{why}: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        }

        // A silently de-duplicated write would return the same 200, so read back to prove none stored.
        foreach (var (key, why, _, _) in rejections)
        {
            var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true });
            Ensure(
                got.Item is null,
                $"the write refused for {why} must leave nothing behind -- in particular it must not be silently de-duplicated and stored -- got {Py.Repr(got.Item)}");
        }

        // --- the same duplicate-member rule applies off a ConditionExpression value ... ---
        const string expectedCondMessage = invalid + "Input collection [\"z\", \"z\"] contains duplicates.";
        var (condCode, condMessage) = await ErrorCode(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = t,
                Item = Item(("pk", S("ok-ss")), ("s", SS("c", "d"))),
                ConditionExpression = "contains(:dup, :needle)",
                ExpressionAttributeValues = Item((":dup", SS("z", "z")), (":needle", S("z"))),
            }),
            "PutItem whose ConditionExpression value is a string set with a repeated member");
        Ensure(condCode == ErrValidation, $"a duplicate-member set used as a ConditionExpression value must be refused {ErrValidation}, got {Py.Repr(condCode)}");
        Ensure(condMessage == expectedCondMessage, $"expected the message {Py.Repr(expectedCondMessage)}, got {Py.Repr(condMessage)}");
        var okSs = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ok-ss"))), ConsistentRead = true })).Item ?? [];
        var okMembers = (okSs.GetValueOrDefault("s")?.SS ?? []).Order(StringComparer.Ordinal).ToList();
        Ensure(
            okMembers.SequenceEqual(["a", "b"]),
            $"the conditional write refused for an invalid ConditionExpression value must leave the existing item untouched, got {Py.Repr(okSs.GetValueOrDefault("s"))}");

        // --- ... and off a GetItem key, where AWS validates the key's set shape before reading ---
        const string expectedKeyMessage = "One or more parameter values were invalid: Input collection [\"a\", \"a\"] contains duplicates.";
        var (keyCode, keyMessage) = await ErrorCode(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", SS("a", "a"))) }),
            "GetItem whose key value is a string set with a repeated member");
        Ensure(keyCode == ErrValidation, $"a duplicate-member set used as a GetItem key value must be refused {ErrValidation}, got {Py.Repr(keyCode)}");
        Ensure(keyMessage == expectedKeyMessage, $"expected the message {Py.Repr(expectedKeyMessage)}, got {Py.Repr(keyMessage)}");

        // --- an empty STRING is a legal member: this is not the "no empty values" rule ---
        await ddb.PutItemAsync(t, Item(("pk", S("ss-empty-member")), ("s", SS("", "a"))));
        var emptyMember = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ss-empty-member"))), ConsistentRead = true })).Item ?? [];
        var ssMembers = emptyMember.GetValueOrDefault("s")?.SS;
        Ensure(ssMembers is not null, $"an empty string is a legal SS member, so this write must be accepted and readable, got {Py.Repr(emptyMember.GetValueOrDefault("s"))}");
        Ensure(ssMembers.Order(StringComparer.Ordinal).SequenceEqual(["", "a"]), $"an SS containing an empty string must round-trip with both members, got {Py.Repr(ssMembers)}");

        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true, Select = Select.COUNT });
        Ensure(
            scanned.Count == 4,
            $"only the three legal set controls and the empty-string-member item may exist after eight refused writes, got {Py.Repr(scanned.Count)} items");
    }

    /// <summary>The AWS error code and message `fn` raises; an unexpected success fails the check.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
        }

        return (error.Code, error.Message);
    }
}
