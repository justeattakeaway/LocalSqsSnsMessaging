// Translated from behavioral/dynamodb/check_delete_item_condition_nested_map_member_existence_gates_the_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-nested-map-member-existence-gates-the-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionNestedMapMemberExistenceGatesTheDelete
{
    private const string Check = "delete-item-condition-nested-map-member-existence-gates-the-delete";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>The subset of the full name map these expression placeholders use.</summary>
    private static Dictionary<string, string> NamesOf(params string[] keys)
    {
        var full = Names(("#doc", "doc"), ("#present", "present"), ("#absent", "absent"));
        return keys.ToDictionary(k => k, k => full[k], StringComparer.Ordinal);
    }

    /// <summary>
    /// DeleteItem's ConditionExpression attribute_exists/attribute_not_exists reach into a nested
    /// map member, not only a top-level attribute, to gate the delete.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-nested-map-member-existence-gates-the-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Get(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        Task<DeleteItemResponse> Delete(Dictionary<string, AttributeValue> key, string condition, Dictionary<string, string> names) =>
            ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = key,
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
            });

        // attribute_exists on a present nested map member.
        var presentKey = Item(("pk", S("di-nested-exists-present")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-nested-exists-present")), ("doc", M(("present", S("here"))))));
        // Break: the nested member named does not exist, so attribute_exists must refuse the delete.
        await ExpectError(() => Delete(presentKey, "attribute_exists(#doc.#absent)", NamesOf("#doc", "#absent")), Ccfe);
        var stillThere = await Get(presentKey);
        Ensure(
            stillThere is not null && Py.Equal(stillThere.GetValueOrDefault("doc"), M(("present", S("here")))),
            $"rejected delete must not remove the item, got {Py.Repr(stillThere)}");
        // Positive control: the condition on the member that IS present holds, and the delete applies.
        await Delete(presentKey, "attribute_exists(#doc.#present)", NamesOf("#doc", "#present"));
        var gone = await Get(presentKey);
        Ensure(gone is null, $"expected item absent after attribute_exists on a present nested member, got {Py.Repr(gone)}");

        // attribute_not_exists on an absent nested map member.
        var absentKey = Item(("pk", S("di-nested-not-exists-absent")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-nested-not-exists-absent")), ("doc", M(("present", S("here"))))));
        // Break: the nested member named IS present, so attribute_not_exists must refuse the delete.
        await ExpectError(() => Delete(absentKey, "attribute_not_exists(#doc.#present)", NamesOf("#doc", "#present")), Ccfe);
        stillThere = await Get(absentKey);
        Ensure(
            stillThere is not null && Py.Equal(stillThere.GetValueOrDefault("doc"), M(("present", S("here")))),
            $"rejected delete must not remove the item, got {Py.Repr(stillThere)}");
        // Positive control: the condition on the member that is absent holds, and the delete applies.
        await Delete(absentKey, "attribute_not_exists(#doc.#absent)", NamesOf("#doc", "#absent"));
        gone = await Get(absentKey);
        Ensure(gone is null, $"expected item absent after attribute_not_exists on an absent nested member, got {Py.Repr(gone)}");
    }

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
