// Translated from behavioral/dynamodb/check_put_item_condition_attribute_exists_and_contains_set_member.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-attribute-exists-and-contains-set-member</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionAttributeExistsAndContainsSetMember
{
    private const string Check = "put-item-condition-attribute-exists-and-contains-set-member";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// PutItem's `attribute_exists(pk) AND contains(members, :v)` replaces the item only when it
    /// already exists and its string set holds :v by value and type; it is refused with
    /// ConditionalCheckFailedException, item unchanged, when the item is absent or the set lacks :v.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-attribute-exists-and-contains-set-member")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-put-exists-contains-");
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

        Dictionary<string, string> NamesMap() => Names(("#m", "members"));
        const string cond = "attribute_exists(pk) AND contains(#m, :v)";

        async Task<Dictionary<string, AttributeValue>?> Read(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // --- the item does not exist yet: attribute_exists(pk) is false, so the AND is refused
        // without raising a type error for evaluating contains() against a missing attribute.
        var absentKey = Item(("pk", S("absent")));
        var (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("absent")), ("members", SS("ignored")), ("written", Bool(true))),
            ConditionExpression = cond,
            ExpressionAttributeNames = NamesMap(),
            ExpressionAttributeValues = Item((":v", S("ignored"))),
        }));
        Ensure((code, msg) == Ccf, $"PutItem on an absent item under {Py.Repr(cond)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(await Read(absentKey) is null, "PutItem under a failing condition must not create the item");

        // --- the item exists but :v is not a member of the set: contains() is false.
        var presentKey = Item(("pk", S("present")));
        var original = Item(("pk", S("present")), ("members", SS("admin", "viewer")));
        await ddb.PutItemAsync(table, original);

        (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("present")), ("members", SS("admin", "viewer")), ("written", Bool(true))),
            ConditionExpression = cond,
            ExpressionAttributeNames = NamesMap(),
            ExpressionAttributeValues = Item((":v", S("owner"))),
        }));
        Ensure((code, msg) == Ccf, $"PutItem under a failing contains() condition gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var got = await Read(presentKey);
        Ensure(
            Py.Equal(UnorderedSets(got), UnorderedSets(original)),
            $"PutItem under a failing contains() condition left {Py.Repr(got)}, expected {Py.Repr(original)}");

        // --- positive control: the item exists and :v is a member of the set by value and type,
        // so both arms hold and the write replaces the item.
        var replacement = Item(("pk", S("present")), ("members", SS("admin", "viewer")), ("written", Bool(true)));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = replacement,
            ConditionExpression = cond,
            ExpressionAttributeNames = NamesMap(),
            ExpressionAttributeValues = Item((":v", S("admin"))),
        });
        got = await Read(presentKey);
        Ensure(
            Py.Equal(UnorderedSets(got), UnorderedSets(replacement)),
            $"PutItem under a holding condition left {Py.Repr(got)}, expected {Py.Repr(replacement)}");
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

    /// <summary>The (code, message) of the ClientError <c>fn()</c> raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
