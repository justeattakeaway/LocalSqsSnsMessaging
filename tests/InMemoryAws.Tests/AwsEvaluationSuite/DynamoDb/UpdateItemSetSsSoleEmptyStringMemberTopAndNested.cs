// Translated from behavioral/dynamodb/check_update_item_set_ss_sole_empty_string_member_top_and_nested.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-ss-sole-empty-string-member-top-and-nested</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetSsSoleEmptyStringMemberTopAndNested
{
    private const string Check = "update-item-set-ss-sole-empty-string-member-top-and-nested";

    /// <summary>
    /// UpdateItem SET of a string set whose only member is the empty string stores it,
    /// both as a top-level attribute and at a nested document path.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-ss-sole-empty-string-member-top-and-nested")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
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

        await ddb.PutItemAsync(t, Item(("pk", S("x")), ("wrapper", M())));

        var updated = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("x"))),
            UpdateExpression = "SET #tg = :e, #w.#tg = :e",
            ExpressionAttributeNames = Names(("#tg", "tags"), ("#w", "wrapper")),
            ExpressionAttributeValues = Item((":e", SS(""))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(Get(updated, "tags"), SS("")),
            "SET #tg = :e with :e={'SS': ['']} must store the set at the top level, " +
            $"got {Py.Repr(Get(updated, "tags"))}");
        Ensure(
            Py.Equal(Get(Get(updated, "wrapper")?.M, "tags"), SS("")),
            "SET #w.#tg = :e with :e={'SS': ['']} must store the set at the nested path, " +
            $"got {Py.Repr(Get(updated, "wrapper"))}");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            Py.Equal(Get(got, "tags"), SS("")),
            $"a later GetItem must read back the top-level set as {{'SS': ['']}}, got {Py.Repr(Get(got, "tags"))}");
        Ensure(
            Py.Equal(Get(Get(got, "wrapper")?.M, "tags"), SS("")),
            "a later GetItem must read back the nested set as {'SS': ['']}, " +
            $"got {Py.Repr(Get(got, "wrapper"))}");
    }

    private static AttributeValue? Get(Dictionary<string, AttributeValue>? item, string name) =>
        item is not null && item.TryGetValue(name, out var value) ? value : null;
}
