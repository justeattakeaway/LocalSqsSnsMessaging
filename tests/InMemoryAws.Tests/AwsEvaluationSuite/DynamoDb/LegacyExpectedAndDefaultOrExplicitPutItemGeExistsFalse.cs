// Translated from behavioral/dynamodb/check_legacy_expected_and_default_or_explicit_put_item_ge_exists_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-expected-and-default-or-explicit-put-item-ge-exists-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyExpectedAndDefaultOrExplicitPutItemGeExistsFalse
{
    private const string CheckId = "legacy-expected-and-default-or-explicit-put-item-ge-exists-false";
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>A two-condition legacy Expected: score GE threshold, and extra must not exist.</summary>
    private static Dictionary<string, ExpectedAttributeValue> Expected(string threshold) => new(StringComparer.Ordinal)
    {
        ["score"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.GE, AttributeValueList = [N(threshold)] },
        ["extra"] = new ExpectedAttributeValue { Exists = false },
    };

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success raises too.</summary>
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

    /// <summary>
    /// A two-condition legacy Expected (score GE threshold, extra Exists:false) on PutItem is
    /// ANDed when ConditionalOperator is omitted and ORed under ConditionalOperator='OR': AND needs
    /// both true or raises ConditionalCheckFailedException; OR lands when either is true.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.ConditionalOperator.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-expected-and-default-or-explicit-put-item-ge-exists-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-legcondput-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task Seed(string pk, string score, bool extraPresent)
        {
            var item = Item(("pk", S(pk)), ("score", N(score)));
            if (extraPresent)
            {
                item["extra"] = S("present");
            }

            await ddb.PutItemAsync(table, item);
        }

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // --- default AND (ConditionalOperator omitted): both conditions true -> lands ---
        await Seed("and-pass", "50", extraPresent: false); // score GE 10 true, extra Exists:false true
        var replacement = Item(("pk", S("and-pass")), ("score", N("50")), ("note", S("replaced")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = replacement, Expected = Expected("10") });
        var got = await Get("and-pass");
        Ensure(Py.Equal(got, replacement), $"PutItem default-AND with both conditions true did not land: {Py.Repr(got)}");

        // --- default AND: extra present makes Exists:false false -> refused, nothing changes ---
        var original = Item(("pk", S("and-fail")), ("score", N("50")), ("extra", S("present")));
        await Seed("and-fail", "50", extraPresent: true);
        await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("and-fail")), ("score", N("50")), ("extra", S("present")), ("note", S("replaced"))),
                Expected = Expected("10"),
            }),
            Ccfe);
        got = await Get("and-fail");
        Ensure(Py.Equal(got, original), $"PutItem default-AND refusal must leave the item untouched: {Py.Repr(got)}");

        // --- explicit OR: score GE 10 false, extra Exists:false true -> exactly one true, lands ---
        await Seed("or-pass", "5", extraPresent: false);
        var replacementOr = Item(("pk", S("or-pass")), ("score", N("5")), ("note", S("replaced")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = replacementOr, Expected = Expected("10"), ConditionalOperator = ConditionalOperator.OR });
        got = await Get("or-pass");
        Ensure(Py.Equal(got, replacementOr), $"PutItem OR with exactly one condition true did not land: {Py.Repr(got)}");
    }
}
