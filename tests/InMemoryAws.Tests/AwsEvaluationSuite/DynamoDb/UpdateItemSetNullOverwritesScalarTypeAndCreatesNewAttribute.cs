// Translated from behavioral/dynamodb/check_update_item_set_null_overwrites_scalar_type_and_creates_new_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-null-overwrites-scalar-type-and-creates-new-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetNullOverwritesScalarTypeAndCreatesNewAttribute
{
    private const string Check = "update-item-set-null-overwrites-scalar-type-and-creates-new-attribute";

    /// <summary>
    /// UpdateExpression SET writes a top-level NULL-typed value both onto a brand new attribute
    /// and over an existing String one, and the stored type is exactly NULL, not a truthy stand-in.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-null-overwrites-scalar-type-and-creates-new-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
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

        var key = Item(("pk", S("x")));
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("note", S("hello")), ("count", N("3"))));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        var newValues = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET note = :null, extra = :null",
            ExpressionAttributeValues = Item((":null", Null())),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(newValues, Item(("note", Null()), ("extra", Null()))),
            $"UpdateItem UPDATED_NEW: expected note and extra both NULL, got {Py.Repr(newValues)}");

        var got = await Read();
        Ensure(
            Py.Equal(got, Item(("pk", S("x")), ("note", Null()), ("extra", Null()), ("count", N("3")))),
            $"GetItem after SET to NULL: expected note/extra NULL and count untouched, got {Py.Repr(got)}");

        // The stored type is exactly NULL: a condition that only an attribute_type NULL match
        // satisfies must pass for `extra`, proving it is typed NULL rather than merely absent-ish.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #c = #c + :one",
            ConditionExpression = "attribute_type(extra, :t)",
            ExpressionAttributeNames = Names(("#c", "count")),
            ExpressionAttributeValues = Item((":one", N("1")), (":t", S("NULL"))),
        });
        var bumped = await Read();
        Ensure(
            Py.Equal(bumped["count"], N("4")),
            "the conditional increment gated on attribute_type(extra, NULL) should have applied, " +
            $"got count {Py.Repr(bumped["count"])}");

        var oldValues = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE note, extra",
            ReturnValues = ReturnValue.UPDATED_OLD,
        })).Attributes;
        Ensure(
            Py.Equal(oldValues, Item(("note", Null()), ("extra", Null()))),
            $"UpdateItem UPDATED_OLD after REMOVE: expected both prior values NULL, got {Py.Repr(oldValues)}");

        var final = await Read();
        Ensure(
            Py.Equal(final, Item(("pk", S("x")), ("count", N("4")))),
            $"GetItem after REMOVE: expected only pk and count left, got {Py.Repr(final)}");
    }
}
