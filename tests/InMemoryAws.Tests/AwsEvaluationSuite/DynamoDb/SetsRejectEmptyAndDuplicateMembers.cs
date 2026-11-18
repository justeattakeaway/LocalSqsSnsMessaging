// Translated from behavioral/dynamodb/check_sets_reject_empty_and_duplicate_members.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.sets-reject-empty-and-duplicate-members</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetsRejectEmptyAndDuplicateMembers
{
    private const string CheckId = "sets-reject-empty-and-duplicate-members";
    private const string ErrValidation = "ValidationException";
    private static readonly string[] Kinds = ["SS", "NS", "BS"];

    /// <summary>The error code and message <paramref name="fn"/> raises; an unexpected success fails loudly.</summary>
    private static async Task<(string? Code, string? Message)> Code(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("real AWS rejects this call, but it SUCCEEDED and returned {}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>A set attribute of <paramref name="kind"/> with <paramref name="members"/> (strings, or byte values for BS).</summary>
    private static AttributeValue Set(string kind, params object[] members) => kind switch
    {
        "SS" => new AttributeValue { SS = [.. members.Cast<string>()] },
        "NS" => new AttributeValue { NS = [.. members.Cast<string>()] },
        _ => new AttributeValue { BS = [.. members.Cast<byte[]>().Select(b => new MemoryStream(b))] },
    };

    private static AttributeValue Legal(string kind) => kind switch
    {
        "SS" => Set(kind, "a", "b"),
        "NS" => Set(kind, "1", "2"),
        _ => Set(kind, new byte[] { 0x01 }, new byte[] { 0x02 }),
    };

    private static AttributeValue Empty(string kind) => Set(kind);

    private static AttributeValue Dupes(string kind) => kind switch
    {
        "SS" => Set(kind, "a", "a"),
        "NS" => Set(kind, "1", "1"),
        _ => Set(kind, new byte[] { 0x01 }, new byte[] { 0x01 }),
    };

    /// <summary>
    /// SS, NS and BS are sets, not lists: an empty set and a set with a duplicate member are rejected
    /// with ValidationException for all three types (also when nested in a map), and nothing is stored.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.sets-reject-empty-and-duplicate-members")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // AWS words each refusal differently per set type, down to spacing and punctuation.
        const string invalid = "1 validation error detected: One or more parameter values were invalid: ";
        var emptyMessage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SS"] = invalid + "An string set  may not be empty",
            ["NS"] = invalid + "An number set  may not be empty",
            ["BS"] = invalid + "Binary sets should not be empty",
        };
        var dupeMessage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SS"] = invalid + "Input collection [\"a\", \"a\"] contains duplicates.",
            ["NS"] = invalid + "Input collection contains duplicates.",
            ["BS"] = invalid + "Input collection [\"AQ==\",\"AQ==\"]of type BS contains duplicates.",
        };

        async Task<Dictionary<string, AttributeValue>?> Get(string key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;

        foreach (var kind in Kinds)
        {
            var key = $"ok-{kind}";
            await ddb.PutItemAsync(t, Item(("pk", S(key)), ("s", Legal(kind))));
            var got = await Get(key) ?? [];
            var s = got.GetValueOrDefault("s");
            var stored = s is null ? null : (Py.Boto(s).GetValueOrDefault(kind) as System.Collections.ICollection);
            Ensure(stored is not null, $"positive control failed: a legal {kind} must round-trip, got {Py.Repr(s)}");
            Ensure(stored.Count == 2, $"a 2-member {kind} must keep both members, got {Py.Repr(stored)}");
        }

        foreach (var kind in Kinds)
        {
            var key = $"empty-{kind}";
            var (code, message) = await Code(() => ddb.PutItemAsync(t, Item(("pk", S(key)), ("s", Empty(kind)))));
            Ensure(code == ErrValidation, $"an empty {kind} must be {ErrValidation}, got {Py.Repr(code)}");
            Ensure(
                message == emptyMessage[kind],
                $"an empty {kind}: expected the message {Py.Repr(emptyMessage[kind])}, got {Py.Repr(message)}");
            Ensure(await Get(key) is null, $"a rejected empty {kind} must not be stored");
        }

        foreach (var kind in Kinds)
        {
            var key = $"dupe-{kind}";
            var (code, message) = await Code(() => ddb.PutItemAsync(t, Item(("pk", S(key)), ("s", Dupes(kind)))));
            Ensure(
                code == ErrValidation,
                $"a {kind} containing a duplicate must be {ErrValidation} rather than being silently deduplicated or stored as-is, got {Py.Repr(code)}");
            Ensure(
                message == dupeMessage[kind],
                $"a {kind} containing a duplicate: expected the message {Py.Repr(dupeMessage[kind])}, got {Py.Repr(message)}");
            Ensure(await Get(key) is null, $"a rejected duplicate-bearing {kind} must not be stored");
        }

        // A set nested inside a map is held to the same rule.
        var (nestedCode, nestedMessage) = await Code(() => ddb.PutItemAsync(t, Item(
            ("pk", S("nested-dupe")),
            ("m", M(("s", SS("z", "z")))))));
        Ensure(nestedCode == ErrValidation, $"a duplicate-bearing SS nested in a map must be {ErrValidation}, got {Py.Repr(nestedCode)}");
        Ensure(
            nestedMessage == invalid + "Input collection [\"z\", \"z\"] contains duplicates.",
            $"a duplicate-bearing SS nested in a map: got the message {Py.Repr(nestedMessage)}");
    }
}
