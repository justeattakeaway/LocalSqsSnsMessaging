// Translated from behavioral/dynamodb/check_put_item_binary_set_zero_length_member_round_trips.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-binary-set-zero-length-member-round-trips</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemBinarySetZeroLengthMemberRoundTrips
{
    private const string CheckId = "put-item-binary-set-zero-length-member-round-trips";

    /// <summary>bytes sort as Python sorts them: lexicographically, unsigned.</summary>
    private static List<byte[]> SortedBytes(IEnumerable<byte[]> members)
    {
        var list = members.ToList();
        list.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        return list;
    }

    /// <summary>
    /// PutItem accepts a Binary Set holding a zero-length member on its own, and alongside a
    /// non-empty member, and GetItem reads the empty member back intact.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-binary-set-zero-length-member-round-trips")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-bszero-");
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

        await ddb.PutItemAsync(t, Item(("pk", S("solo")), ("s", BS(Array.Empty<byte>()))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("solo"))), ConsistentRead = true })).Item ?? [];
        var members = got.GetValueOrDefault("s")?.BS?.Select(b => b.ToArray()).ToList();
        Ensure(
            members is not null && members.Count == 1 && members[0].Length == 0,
            $"a BS holding only a zero-length member must round-trip as [b\"\"], got {Py.Repr(members)}");

        await ddb.PutItemAsync(t, Item(("pk", S("mixed")), ("s", BS([], [0x01]))));
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("mixed"))), ConsistentRead = true })).Item ?? [];
        members = got.GetValueOrDefault("s")?.BS?.Select(b => b.ToArray()).ToList();
        Ensure(
            Py.Equal(SortedBytes(members ?? []), SortedBytes([[], [0x01]])),
            $"a BS mixing a zero-length member with b\"\\x01\" must keep both, got {Py.Repr(members)}");

        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true, Select = Select.COUNT });
        Ensure(scanned.Count == 2, $"only the two items just written may exist, got {scanned.Count} items");
    }
}
