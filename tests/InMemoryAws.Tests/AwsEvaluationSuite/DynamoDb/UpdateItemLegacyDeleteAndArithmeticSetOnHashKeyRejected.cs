// Translated from behavioral/dynamodb/check_update_item_legacy_delete_and_arithmetic_set_on_hash_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-legacy-delete-and-arithmetic-set-on-hash-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemLegacyDeleteAndArithmeticSetOnHashKeyRejected
{
    private const string Check = "update-item-legacy-delete-and-arithmetic-set-on-hash-key-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The Error dict of a call real AWS refuses; an unexpected success raises.</summary>
    private static async Task<AwsError> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err ?? throw Fail($"real AWS rejects this UpdateItem, but it SUCCEEDED and returned {result}");
    }

    private static Dictionary<string, string?> Dict(AwsError err) => new() { ["Code"] = err.Code, ["Message"] = err.Message };

    /// <summary>
    /// UpdateItem rejects a legacy AttributeUpdates DELETE naming the hash key and a
    /// multi-clause UpdateExpression whose arithmetic SET clause targets the hash key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-legacy-delete-and-arithmetic-set-on-hash-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-hkdel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("hk-item")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("hk-item")),
            ("note", S("orig-note")),
            ("tally", N("1")),
            ("m", M(("b", N("2")))),
            ("lst", L(N("10"), N("20")))));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // Positive control: a multi-clause UpdateExpression with an arithmetic clause on
        // non-key attributes succeeds, so the rejections below are specific to touching pk.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET note = :v1, tally = tally + :v2",
            ExpressionAttributeValues = Item((":v1", S("updated-note")), (":v2", N("4"))),
        });
        var afterControl = await Read();
        Ensure(
            Py.Equal(afterControl["note"], S("updated-note")),
            $"positive control: expected note=updated-note, got {Py.Repr(afterControl.GetValueOrDefault("note"))}");
        Ensure(
            Py.Equal(afterControl["tally"], N("5")),
            $"positive control: expected tally=5, got {Py.Repr(afterControl.GetValueOrDefault("tally"))}");

        var err = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            AttributeUpdates = new() { ["pk"] = new AttributeValueUpdate { Action = AttributeAction.DELETE } },
        }));
        Ensure(err.Code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(Dict(err))}");
        const string expectedKeyMsg =
            "One or more parameter values were invalid: Cannot update attribute pk. " +
            "This attribute is part of the key";
        Ensure(err.Message == expectedKeyMsg, $"expected message {Py.Repr(expectedKeyMsg)}, got {Py.Repr(err.Message)}");

        var afterDelete = await Read();
        Ensure(
            Py.Equal(afterDelete, afterControl),
            $"the refused legacy DELETE on pk must leave the item unchanged, got {Py.Repr(afterDelete)}");

        var err2 = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET note = :v3, pk = m.b + lst[1]",
            ExpressionAttributeValues = Item((":v3", S("should-not-apply"))),
        }));
        Ensure(err2.Code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(Dict(err2))}");
        Ensure(err2.Message == expectedKeyMsg, $"expected message {Py.Repr(expectedKeyMsg)}, got {Py.Repr(err2.Message)}");

        var afterArith = await Read();
        Ensure(
            Py.Equal(afterArith, afterControl),
            "the refused multi-clause arithmetic SET on pk must leave the item unchanged " +
            $"(including the note clause in the same request), got {Py.Repr(afterArith)}");
    }
}
