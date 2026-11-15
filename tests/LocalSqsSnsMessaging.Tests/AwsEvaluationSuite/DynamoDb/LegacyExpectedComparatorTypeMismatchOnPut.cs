// Translated from behavioral/dynamodb/check_legacy_expected_comparator_type_mismatch_on_put.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-expected-comparator-type-mismatch-on-put</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyExpectedComparatorTypeMismatchOnPut
{
    private const string CheckId = "legacy-expected-comparator-type-mismatch-on-put";
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    // A stored attribute compared with a legacy ComparisonOperator condition whose
    // AttributeValueList element is a different type never matches for every comparator
    // except NE, which treats the type mismatch itself as inequality.
    private static readonly string[] NeverMatches = ["EQ", "LE", "LT", "GE", "GT"];

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error is null
            ? throw Fail("expected the conditional PutItem to be rejected, but it succeeded: None")
            : (error.Code, error.Message);
    }

    private static async Task ExpectError(Func<Task> fn, (string Code, string Message) expected)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    /// <summary>
    /// A legacy Expected ComparisonOperator condition whose value type differs from the stored
    /// attribute's never matches, so PutItem fails with ConditionalCheckFailedException, for every
    /// comparator except NE, where the type mismatch itself satisfies "not equal".
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.Conditions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-expected-comparator-type-mismatch-on-put")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbtm-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        async Task<Dictionary<string, AttributeValue>?> Stored(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        async Task TryConditionalPut(string pk, ExpectedAttributeValue condition)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("num", N("6"))));
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(pk)), ("num", N("6")), ("marker", S("applied"))),
                Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal) { ["num"] = condition },
            });
        }

        // S "6" compared against a stored N "6": EQ, LE, LT, GE, GT and BETWEEN all reject the
        // conditional write, and the pre-existing item is left exactly as it was.
        foreach (var op in NeverMatches)
        {
            var opPk = $"s-vs-n-{op.ToLowerInvariant()}";
            await ExpectError(
                () => TryConditionalPut(opPk, new ExpectedAttributeValue { ComparisonOperator = op, AttributeValueList = [S("6")] }),
                Ccfe);
            var opUnchanged = await Stored(opPk);
            Ensure(Py.Equal(opUnchanged, Item(("pk", S(opPk)), ("num", N("6")))), $"{op}: a rejected conditional PutItem changed the stored item: {Py.Repr(opUnchanged)}");
        }

        var pk = "s-vs-n-between";
        await ExpectError(
            () => TryConditionalPut(pk, new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.BETWEEN, AttributeValueList = [S("0"), S("9")] }),
            Ccfe);
        var unchanged = await Stored(pk);
        Ensure(Py.Equal(unchanged, Item(("pk", S(pk)), ("num", N("6")))), $"BETWEEN: a rejected conditional PutItem changed the stored item: {Py.Repr(unchanged)}");

        // NE on the same type mismatch: AWS treats the attribute as "not equal" to a value of a
        // different type, so the condition is satisfied and the write lands.
        pk = "s-vs-n-ne";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("num", N("6"))));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(pk)), ("num", N("6")), ("marker", S("applied"))),
            Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal)
            {
                ["num"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.NE, AttributeValueList = [S("6")] },
            },
        });
        var landed = await Stored(pk);
        Ensure(Py.Equal(landed, Item(("pk", S(pk)), ("num", N("6")), ("marker", S("applied")))), $"NE: a satisfied conditional PutItem did not land: {Py.Repr(landed)}");

        // A number compared against a number SET of the same member is the same rule: EQ never
        // matches a set-typed value, NE does.
        pk = "n-vs-ns-eq";
        await ExpectError(
            () => TryConditionalPut(pk, new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [NS("6")] }),
            Ccfe);
        unchanged = await Stored(pk);
        Ensure(Py.Equal(unchanged, Item(("pk", S(pk)), ("num", N("6")))), $"EQ N-vs-NS: a rejected conditional PutItem changed the stored item: {Py.Repr(unchanged)}");

        pk = "n-vs-ns-ne";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("num", N("6"))));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(pk)), ("num", N("6")), ("marker", S("applied"))),
            Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal)
            {
                ["num"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.NE, AttributeValueList = [NS("6")] },
            },
        });
        landed = await Stored(pk);
        Ensure(Py.Equal(landed, Item(("pk", S(pk)), ("num", N("6")), ("marker", S("applied")))), $"NE N-vs-NS: a satisfied conditional PutItem did not land: {Py.Repr(landed)}");

        // Positive control: the comparator mechanism itself works -- a matched type condition
        // (EQ against the same N value) succeeds, so the rejections above are not vacuous.
        pk = "matched-type-eq";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("num", N("6"))));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(pk)), ("num", N("6")), ("marker", S("applied"))),
            Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal)
            {
                ["num"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [N("6")] },
            },
        });
        landed = await Stored(pk);
        Ensure(Py.Equal(landed, Item(("pk", S(pk)), ("num", N("6")), ("marker", S("applied")))), $"positive control: a matched-type EQ condition did not let the write land: {Py.Repr(landed)}");
    }
}
