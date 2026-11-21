// Translated from behavioral/dynamodb/check_scan_filter_contains_exact_member_and_size_of_bs_and_ns.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-contains-exact-member-and-size-of-bs-and-ns</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterContainsExactMemberAndSizeOfBsAndNs
{
    private const string Check = "scan-filter-contains-exact-member-and-size-of-bs-and-ns";

    /// <summary>
    /// pk -> {bs: BinarySet, ns: NumberSet}. "other" carries every member of "match" plus one
    /// extra of each type, so a count-based or membership-based filter that picked the wrong
    /// metric would pick the wrong row or both.
    /// </summary>
    private static readonly (string Pk, (string Name, AttributeValue Value)[] Attrs)[] Items =
    [
        ("match", [("bs", BS([0x01], [0x02], [0x03])), ("ns", NS("1", "2", "42"))]),
        ("other", [("bs", BS([0x01], [0x02], [0x03], [0x04])), ("ns", NS("1", "2", "42", "100"))]),
    ];

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>
    /// A Scan FilterExpression's contains() matches a Binary Set only by an exact member, never
    /// a byte-extension of one, and size() of a Binary Set or Number Set equals its member count.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-contains-exact-member-and-size-of-bs-and-ns")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbbsns-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (pk, attrs) in Items)
        {
            await ddb.PutItemAsync(table, Item([("pk", S(pk)), .. attrs]));
        }

        async Task<SortedSet<string>> ScanPks(string expr, Dictionary<string, AttributeValue> values)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = expr,
                ExpressionAttributeValues = values,
            });
            return new SortedSet<string>(resp.Items.Select(i => i["pk"].S), StringComparer.Ordinal);
        }

        // contains() on a Binary Set matches exactly the item whose set holds that exact member.
        var onlyOther = await ScanPks("contains(bs, :v)", Item((":v", B([0x04]))));
        Ensure(onlyOther.SetEquals(["other"]), $"contains(bs, (B)0x04) selected {Py.Repr(onlyOther)}, expected only ['other']");

        // A byte-extension of a member is not itself a member: contains() tests set membership by
        // exact equality, never a byte-prefix or substring match the way scalar Binary contains() does.
        var extension = await ScanPks("contains(bs, :v)", Item((":v", B([0x04, 0x00]))));
        Ensure(
            extension.Count == 0,
            $"contains(bs, (B)0x0400) selected {Py.Repr(extension)}, expected none: 0x0400 is not a " +
            "member of either set even though it extends a member byte-for-byte");

        // Positive control: a member shared by both items' sets matches both, proving the mechanism
        // discriminates by exact membership rather than by excluding "other" outright.
        var both = await ScanPks("contains(bs, :v)", Item((":v", B([0x01]))));
        Ensure(
            both.SetEquals(["match", "other"]),
            $"contains(bs, (B)0x01) selected {Py.Repr(both)}, expected both items: 0x01 is a member " +
            "of each set");

        // size() of a Binary Set or Number Set equals its member count, not a byte or digit count.
        foreach (var attr in new[] { "bs", "ns" })
        {
            var got3 = await ScanPks($"size({attr}) = :n", Item((":n", N("3"))));
            Ensure(got3.SetEquals(["match"]), $"size({attr}) = 3 selected {Py.Repr(got3)}, expected ['match']");
            var got4 = await ScanPks($"size({attr}) = :n", Item((":n", N("4"))));
            Ensure(got4.SetEquals(["other"]), $"size({attr}) = 4 selected {Py.Repr(got4)}, expected ['other']");
        }
    }
}
