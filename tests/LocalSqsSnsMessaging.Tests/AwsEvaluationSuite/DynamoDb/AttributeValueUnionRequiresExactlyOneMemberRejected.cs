// Translated from behavioral/dynamodb/check_attribute_value_union_requires_exactly_one_member_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.attribute-value-union-requires-exactly-one-member-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AttributeValueUnionRequiresExactlyOneMemberRejected
{
    private const string Check = "attribute-value-union-requires-exactly-one-member-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// An AttributeValue with zero or two type members is refused ValidationException at every depth
    /// (top level, inside an M, inside an L) with the exact message, and no refused write lands.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.attribute-value-union-requires-exactly-one-member-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-union-");
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

        // Positive control: the same shapes with arity exactly one are accepted and round-trip, so the
        // rejections below are attributable to the arity and not to the nesting or to the table.
        await ddb.PutItemAsync(t, Item(
            ("pk", S("legal")),
            ("flat", S("x")),
            ("nested", M(("inner", S("x")))),
            ("listed", L(S("x"), N("1")))));
        var ctl = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("legal"))), ConsistentRead = true })).Item ?? [];
        Ensure(Py.Equal(ctl.GetValueOrDefault("flat"), S("x")), $"control: flat single-member value, got {Py.Repr(ctl.GetValueOrDefault("flat"))}");
        Ensure(
            Py.Equal(ctl.GetValueOrDefault("nested"), M(("inner", S("x")))),
            $"control: single-member value nested in an M, got {Py.Repr(ctl.GetValueOrDefault("nested"))}");
        Ensure(
            Py.Equal(ctl.GetValueOrDefault("listed"), L(S("x"), N("1"))),
            $"control: single-member values inside an L, got {Py.Repr(ctl.GetValueOrDefault("listed"))}");

        const string empty = "Supplied AttributeValue is empty, must contain exactly one of the supported datatypes";
        const string two = "Supplied AttributeValue has more than one datatypes set, must contain exactly one of " +
            "the supported datatypes";
        // The items as boto3 sends them, raw: the .NET SDK's AttributeValue can't be sent empty or with two members.
        var cases = new (string Key, string Why, string ItemJson, string ExpectedMessage)[]
        {
            ("av-zero", "zero type members at the top of the item",
                """{"pk": {"S": "av-zero"}, "a": {}}""", empty),
            ("av-two", "two type members at the top of the item",
                """{"pk": {"S": "av-two"}, "a": {"S": "x", "N": "1"}}""", two),
            ("av-zero-in-m", "zero type members nested inside an M",
                """{"pk": {"S": "av-zero-in-m"}, "m": {"M": {"inner": {}}}}""", empty),
            ("av-two-in-m", "two type members nested inside an M",
                """{"pk": {"S": "av-two-in-m"}, "m": {"M": {"inner": {"S": "x", "BOOL": true}}}}""", two),
            ("av-zero-in-l", "zero type members as an element of an L",
                """{"pk": {"S": "av-zero-in-l"}, "l": {"L": [{}]}}""", empty),
        };
        foreach (var (_, why, itemJson, expectedMessage) in cases)
        {
            var (code, message) = await ErrorCode(
                ctx,
                $$"""{"TableName": "{{t}}", "Item": {{itemJson}}}""",
                $"PutItem with an AttributeValue carrying {why}");
            Ensure(
                code == ErrValidation,
                $"an AttributeValue with {why} is not a legal union value and must be refused " +
                $"{ErrValidation}, got {Py.Repr(code)}");
            Ensure(
                message == expectedMessage,
                $"an AttributeValue with {why}: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        }

        // Effect absent: a read finds nothing at each refused key, so no malformed union was coerced
        // into a stored item.
        foreach (var (key, why, _, _) in cases)
        {
            var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true });
            Ensure(
                got.Item is null,
                $"the write refused for {why} must leave nothing behind at pk={Py.Repr(key)}, got " +
                $"{Py.Repr(got.Item)}");
        }

        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true, Select = Select.COUNT });
        Ensure(
            scanned.Count == 1,
            "only the single control item may exist after five refused writes, got " +
            $"{Py.Repr(scanned.Count)} items");
    }

    /// <summary>Return the error code and message a PutItem of <paramref name="body"/> raises; raise if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(FidelityContext ctx, string body, string what)
    {
        var resp = await ctx.DynamoDbRaw("PutItem", body);
        if ((int)resp.StatusCode < 300)
        {
            throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
        }

        return (resp.ErrorCode, resp.ErrorMessage);
    }
}
