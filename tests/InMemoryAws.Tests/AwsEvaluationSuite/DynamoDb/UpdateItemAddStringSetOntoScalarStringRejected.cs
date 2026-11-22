// Translated from behavioral/dynamodb/check_update_item_add_string_set_onto_scalar_string_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-string-set-onto-scalar-string-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddStringSetOntoScalarStringRejected
{
    private const string Check = "update-item-add-string-set-onto-scalar-string-rejected";

    private static readonly (string Code, string Message) IncorrectOperandType = (
        "ValidationException",
        "An operand in the update expression has an incorrect data type");

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success raises too.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected {expected.Code} but the call succeeded: {result}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary>
    /// UpdateItem ADD of a String Set value onto an existing scalar String attribute is
    /// rejected with ValidationException and leaves the attribute unchanged, while an ADD of a
    /// same-typed String Set value onto a sibling String Set attribute still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-string-set-onto-scalar-string-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);

        var pk = FidelityContext.UniqueName("item-");
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("tag", S("not-a-set")), ("tags", SS("a", "b"))));

        // Positive control: ADD of a String Set onto a sibling String Set attribute succeeds.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "ADD tags :s",
            ExpressionAttributeValues = Item((":s", SS("c"))),
        });
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(
            new HashSet<string>(item["tags"].SS, StringComparer.Ordinal).SetEquals(["a", "b", "c"]),
            $"positive-control ADD did not apply, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(pk))),
                UpdateExpression = "ADD tag :s",
                ExpressionAttributeValues = Item((":s", SS("x"))),
            }),
            IncorrectOperandType);

        // Effect absent: both attributes are untouched by the rejected mismatched ADD.
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item["tag"], S("not-a-set")),
            $"rejected ADD must leave the scalar String attribute untouched, got {Py.Repr(item.GetValueOrDefault("tag"))}");
        Ensure(
            new HashSet<string>(item["tags"].SS, StringComparer.Ordinal).SetEquals(["a", "b", "c"]),
            $"rejected ADD on one attribute must not affect a sibling attribute, got {Py.Repr(item.GetValueOrDefault("tags"))}");
    }
}
