// Translated from behavioral/dynamodb/check_putitem_condition_contains_binary_set_member_gates_the_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-contains-binary-set-member-gates-the-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionContainsBinarySetMemberGatesTheWrite
{
    private const string Check = "putitem-condition-contains-binary-set-member-gates-the-write";

    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A PutItem ConditionExpression `contains(#c, :v)` over a BS tests binary-set membership
    /// by exact byte value: it fails, item unchanged, for a binary value absent from the set, and
    /// holds, replacing the item, for a binary value present in it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-contains-binary-set-member-gates-the-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbbs-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("target")));
        byte[] member = [0x01, 0x02];
        byte[] otherMember = [0xaa, 0xbb];
        byte[] notAMember = [0x99, 0x99];
        var initial = Item(("pk", S("target")), ("chunks", BS(member, otherMember)));
        await ddb.PutItemAsync(table, initial);

        Dictionary<string, string> NamesMap() => Names(("#c", "chunks"));

        // not_a_member is absent from the BS by exact byte value, so contains() is false and the
        // conditional write is refused, leaving the item exactly as it was.
        await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("target")), ("chunks", BS(member, otherMember)), ("written", Bool(true))),
                ConditionExpression = "contains(#c, :v)",
                ExpressionAttributeNames = NamesMap(),
                ExpressionAttributeValues = Item((":v", B(notAMember))),
            }),
            CondFailed);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(UnorderedSets(unchanged), UnorderedSets(initial)),
            $"PutItem under a failing contains() condition left {Py.Repr(unchanged)}, expected {Py.Repr(initial)} unchanged");

        // member is present in the BS by exact byte value, so contains() holds and the write
        // replaces the item.
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("target")), ("chunks", BS(member, otherMember)), ("written", Bool(true))),
            ConditionExpression = "contains(#c, :v)",
            ExpressionAttributeNames = NamesMap(),
            ExpressionAttributeValues = Item((":v", B(member))),
        });
        var replaced = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var expected = Item(("pk", S("target")), ("chunks", BS(member, otherMember)), ("written", Bool(true)));
        Ensure(
            Py.Equal(UnorderedSets(replaced), UnorderedSets(expected)),
            $"PutItem under a holding contains() condition left {Py.Repr(replaced)}, expected {Py.Repr(expected)}");
    }

    /// <summary><paramref name="item"/> with every set member list sorted: DynamoDB sets carry no member order.</summary>
    private static Dictionary<string, object?>? UnorderedSets(Dictionary<string, AttributeValue>? item) =>
        item?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value switch
            {
                { SS: { } ss } => new Dictionary<string, object?> { ["SS"] = ss.Order(StringComparer.Ordinal).ToList() },
                { NS: { } ns } => new Dictionary<string, object?> { ["NS"] = ns.Order(StringComparer.Ordinal).ToList() },
                { BS: { } bs } => new Dictionary<string, object?> { ["BS"] = bs.Select(b => b.ToArray()).OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToList() },
                var other => (object?)other,
            },
            StringComparer.Ordinal);

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success fails too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
