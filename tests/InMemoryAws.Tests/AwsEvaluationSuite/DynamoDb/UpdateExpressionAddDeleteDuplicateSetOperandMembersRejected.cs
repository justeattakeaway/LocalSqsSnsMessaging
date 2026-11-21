// Translated from behavioral/dynamodb/check_update_expression_add_delete_duplicate_set_operand_members_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-expression-add-delete-duplicate-set-operand-members-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateExpressionAddDeleteDuplicateSetOperandMembersRejected
{
    private const string Check = "update-expression-add-delete-duplicate-set-operand-members-rejected";

    private static readonly (string Code, string Message) AddDupSs = (
        "ValidationException",
        "1 validation error detected: One or more parameter values were invalid: " +
        "Input collection [\"dup\", \"dup\"] contains duplicates.");

    private static readonly (string Code, string Message) DeleteDupBs = (
        "ValidationException",
        "1 validation error detected: One or more parameter values were invalid: " +
        "Input collection [\"bm8=\",\"bm8=\"]of type BS contains duplicates.");

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; raises on success.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>
    /// UpdateItem's ADD and DELETE each reject a set operand literal that repeats a member,
    /// leaving the attribute untouched; a same-shaped operand with no repeat still applies.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-expression-add-delete-duplicate-set-operand-members-rejected")]
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

        var key = Item(("pk", S("item")));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("tags", SS("one")), ("bins", BS("aa"u8.ToArray()))));

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD tags :v",
                ExpressionAttributeValues = Item((":v", SS("dup", "dup"))),
            }),
            AddDupSs);

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "DELETE bins :v",
                ExpressionAttributeValues = Item((":v", BS("no"u8.ToArray(), "no"u8.ToArray()))),
            }),
            DeleteDupBs);

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged["tags"], SS("one")),
            $"the rejected ADD must leave the String Set untouched, got {Py.Repr(unchanged["tags"])}");
        Ensure(
            unchanged["bins"].BS is { } bins && bins.Select(b => Convert.ToBase64String(b.ToArray())).Distinct().SequenceEqual(["YWE="]),
            $"the rejected DELETE must leave the Binary Set untouched, got {Py.Repr(unchanged["bins"])}");

        // Positive control: the identical shape of operand, with no repeated member, applies.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD tags :v",
            ExpressionAttributeValues = Item((":v", SS("two"))),
        });
        var grown = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            grown["tags"].SS is { } tags && new HashSet<string>(tags, StringComparer.Ordinal).SetEquals(["one", "two"]),
            $"the positive-control ADD with no repeated member must apply, got {Py.Repr(grown["tags"])}");
    }
}
