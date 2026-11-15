// Translated from behavioral/dynamodb/check_put_item_nested_set_in_list_or_map_empty_rejected_empty_string_member_round_trips.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-nested-set-in-list-or-map-empty-rejected-empty-string-member-round-trips</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemNestedSetInListOrMapEmptyRejectedEmptyStringMemberRoundTrips
{
    private const string CheckId = "put-item-nested-set-in-list-or-map-empty-rejected-empty-string-member-round-trips";
    private const string Invalid = "1 validation error detected: One or more parameter values were invalid: ";
    private static readonly (string Code, string Message) SsEmpty = ("ValidationException", Invalid + "An string set  may not be empty");
    private static readonly (string Code, string Message) NsEmpty = ("ValidationException", Invalid + "An number set  may not be empty");
    private static readonly (string Code, string Message) BsEmpty = ("ValidationException", Invalid + "Binary sets should not be empty");

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<PutItemResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError(Func<Task<PutItemResponse>> fn, string code, string message)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == code, $"expected {Py.Repr(code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> GetItem(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>
    /// PutItem refuses an empty SS, NS or BS nested inside an L element or an M value with the
    /// type's empty-set ValidationException, and stores nothing at that key; a set
    /// holding an empty-string member nested the same way is accepted and round-trips intact.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-nested-set-in-list-or-map-empty-rejected-empty-string-member-round-trips")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-nestset-");
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

        // An empty set nested inside an L element is refused with the type's empty-set message.
        var emptyInList = new List<(string Pk, AttributeValue Member, (string Code, string Message) Expected)>
        {
            ("ss-in-list", new AttributeValue { SS = [] }, SsEmpty),
            ("ns-in-list", new AttributeValue { NS = [] }, NsEmpty),
            ("bs-in-list", new AttributeValue { BS = [] }, BsEmpty),
        };
        foreach (var (pk, member, (code, message)) in emptyInList)
        {
            var item = Item(("pk", S(pk)), ("attr", L(member)));
            await ExpectError(() => ddb.PutItemAsync(t, item), code, message);
            Ensure(await GetItem(ddb, t, pk) is null, $"a rejected empty set nested in an L must not be stored, pk={Py.Repr(pk)}");
        }

        // An empty set nested inside an M value is refused the same way.
        var emptyInMap = new List<(string Pk, AttributeValue Member, (string Code, string Message) Expected)>
        {
            ("ss-in-map", new AttributeValue { SS = [] }, SsEmpty),
            ("ns-in-map", new AttributeValue { NS = [] }, NsEmpty),
            ("bs-in-map", new AttributeValue { BS = [] }, BsEmpty),
        };
        foreach (var (pk, member, (code, message)) in emptyInMap)
        {
            var item = Item(("pk", S(pk)), ("attr", M(("s", member))));
            await ExpectError(() => ddb.PutItemAsync(t, item), code, message);
            Ensure(await GetItem(ddb, t, pk) is null, $"a rejected empty set nested in an M must not be stored, pk={Py.Repr(pk)}");
        }

        // A set holding an empty-string member, nested the same two ways, is legal and round-trips.
        await ddb.PutItemAsync(t, Item(("pk", S("ss-emptymember-in-list")), ("attr", L(SS("", "x")))));
        var got = await GetItem(ddb, t, "ss-emptymember-in-list");
        Ensure(got is not null, "an SS with an empty-string member nested in an L must be stored");
        var members = got["attr"].L[0].SS;
        Ensure(
            members.Order(StringComparer.Ordinal).SequenceEqual(["", "x"]),
            $"an SS with an empty-string member nested in an L must round-trip both members, got {Py.Repr(members)}");

        await ddb.PutItemAsync(t, Item(("pk", S("ss-emptymember-in-map")), ("attr", M(("s", SS("", "x"))))));
        got = await GetItem(ddb, t, "ss-emptymember-in-map");
        Ensure(got is not null, "an SS with an empty-string member nested in an M must be stored");
        members = got["attr"].M["s"].SS;
        Ensure(
            members.Order(StringComparer.Ordinal).SequenceEqual(["", "x"]),
            $"an SS with an empty-string member nested in an M must round-trip both members, got {Py.Repr(members)}");
    }
}
