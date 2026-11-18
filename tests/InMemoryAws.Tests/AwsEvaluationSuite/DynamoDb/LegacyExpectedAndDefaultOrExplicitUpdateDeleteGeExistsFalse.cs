// Translated from behavioral/dynamodb/check_legacy_expected_and_default_or_explicit_update_delete_ge_exists_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-expected-and-default-or-explicit-update-delete-ge-exists-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyExpectedAndDefaultOrExplicitUpdateDeleteGeExistsFalse
{
    private const string CheckId = "legacy-expected-and-default-or-explicit-update-delete-ge-exists-false";
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>A two-condition legacy Expected: score GE threshold, and extra must not exist.</summary>
    private static Dictionary<string, ExpectedAttributeValue> Expected(string threshold) => new(StringComparer.Ordinal)
    {
        ["score"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.GE, AttributeValueList = [N(threshold)] },
        ["extra"] = new ExpectedAttributeValue { Exists = false },
    };

    private static Dictionary<string, AttributeValueUpdate> Marker() => new(StringComparer.Ordinal)
    {
        ["marker"] = new AttributeValueUpdate { Value = S("applied"), Action = AttributeAction.PUT },
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
    /// Two legacy Expected conditions on UpdateItem/DeleteItem are ANDed when ConditionalOperator
    /// is omitted, and ORed under ConditionalOperator='OR': AND needs both GE and Exists:false true
    /// or raises ConditionalCheckFailedException; OR lands when either is true.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.ConditionalOperator.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-expected-and-default-or-explicit-update-delete-ge-exists-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-legcondupd-");
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

        async Task Put(string pk, string score, bool extraPresent)
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

        // --- UpdateItem: default AND (ConditionalOperator omitted) ---
        await Put("u-and-pass", "50", extraPresent: false); // score GE 10 true, extra Exists:false true
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("u-and-pass"))),
            AttributeUpdates = Marker(),
            Expected = Expected("10"),
        });
        var got = await Get("u-and-pass");
        Ensure(got is not null && Py.Equal(got.GetValueOrDefault("marker"), S("applied")), $"UpdateItem default-AND with both conditions true did not land: {Py.Repr(got)}");

        await Put("u-and-fail", "50", extraPresent: true); // score GE 10 true, extra Exists:false FALSE
        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("u-and-fail"))),
                AttributeUpdates = Marker(),
                Expected = Expected("10"),
            }),
            Ccfe);
        got = await Get("u-and-fail");
        Ensure(got is not null && !got.ContainsKey("marker"), $"UpdateItem default-AND refusal must not have applied the update: {Py.Repr(got)}");

        // --- UpdateItem: explicit OR ---
        await Put("u-or-pass", "5", extraPresent: false); // score GE 10 FALSE, extra Exists:false true
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("u-or-pass"))),
            AttributeUpdates = Marker(),
            Expected = Expected("10"),
            ConditionalOperator = ConditionalOperator.OR,
        });
        got = await Get("u-or-pass");
        Ensure(got is not null && Py.Equal(got.GetValueOrDefault("marker"), S("applied")), $"UpdateItem OR with exactly one condition true did not land: {Py.Repr(got)}");

        // --- DeleteItem: default AND (ConditionalOperator omitted) ---
        await Put("d-and-pass", "50", extraPresent: false);
        await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Item(("pk", S("d-and-pass"))), Expected = Expected("10") });
        Ensure(await Get("d-and-pass") is null, "DeleteItem default-AND with both conditions true left the item");

        await Put("d-and-fail", "50", extraPresent: true);
        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Item(("pk", S("d-and-fail"))), Expected = Expected("10") }),
            Ccfe);
        got = await Get("d-and-fail");
        Ensure(got is not null && Py.Equal(got.GetValueOrDefault("extra"), S("present")), $"DeleteItem default-AND refusal must leave the item untouched: {Py.Repr(got)}");

        // --- DeleteItem: explicit OR ---
        await Put("d-or-pass", "5", extraPresent: false);
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("d-or-pass"))),
            Expected = Expected("10"),
            ConditionalOperator = ConditionalOperator.OR,
        });
        Ensure(await Get("d-or-pass") is null, "DeleteItem OR with exactly one condition true left the item");
    }
}
