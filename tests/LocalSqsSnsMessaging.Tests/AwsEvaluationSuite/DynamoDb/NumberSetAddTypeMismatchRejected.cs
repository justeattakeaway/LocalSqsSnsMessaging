// Translated from behavioral/dynamodb/check_number_set_add_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.number-set-add-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberSetAddTypeMismatchRejected
{
    private const string CheckId = "number-set-add-type-mismatch-rejected";
    private static readonly string[] Ab = ["a", "b"];
    private static readonly string[] Abc = ["a", "b", "c"];

    /// <summary>
    /// UpdateItem ADD of a Number Set (NS) value onto an existing String Set (SS)
    /// attribute is rejected with ValidationException, the attribute is left unchanged,
    /// and an ADD of a same-typed (SS) value on the same attribute still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.number-set-add-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);

        var pk = FidelityContext.UniqueName("item-");
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("tags", SS("a", "b"))));

        var err = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "ADD tags :n",
            ExpressionAttributeValues = Item((":n", NS("1"))),
        }));
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(err.Code)}");
        const string expected = "An operand in the update expression has an incorrect data type";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        // Effect absent: the attribute is untouched by the rejected mismatched ADD.
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(item["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(Ab), "SS attribute was mutated by rejected ADD");

        // Positive control: a same-typed ADD on the same attribute still succeeds.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "ADD tags :n",
            ExpressionAttributeValues = Item((":n", SS("c"))),
        });
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(item["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(Abc), "positive-control ADD did not apply");
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
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        return name;
    }

    /// <summary>The Error dict of a call that MUST be refused; RAISES if the call succeeds.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }
}
