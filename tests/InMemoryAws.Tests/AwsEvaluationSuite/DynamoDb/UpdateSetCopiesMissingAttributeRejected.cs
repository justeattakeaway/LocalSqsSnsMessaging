// Translated from behavioral/dynamodb/check_update_set_copies_missing_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-set-copies-missing-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateSetCopiesMissingAttributeRejected
{
    private const string Check = "update-set-copies-missing-attribute-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;
    private const string MissingAttr = "The provided expression refers to an attribute that does not exist in the item";

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (resp, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"call unexpectedly SUCCEEDED where an error was required: {resp}") : (err.Code, err.Message);
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code == "ResourceInUseException" && clock.Elapsed.TotalSeconds < TeardownWaitSeconds)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
                continue;
            }

            throw error.Exception;
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetSeconds)
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {Py.Repr(TableActiveBudgetSeconds)}s: {Py.Repr(status)}");
    }

    /// <summary>
    /// A SET that copies one attribute's value into another (`SET c2 = b`, no function or
    /// arithmetic) raises ValidationException naming the missing operand when `b` is absent, and
    /// writes nothing; the identical expression succeeds once `b` holds a value.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-set-copies-missing-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(("pk", S("doc")), ("keep", S("k")));
        await ddb.PutItemAsync(table, original);

        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET c2 = b",
        }));
        Ensure(
            code == "ValidationException",
            $"SET c2 = b with no stored b answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == MissingAttr, $"SET c2 = b refusal message is {Py.Repr(message)}, expected {Py.Repr(MissingAttr)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, original),
            $"after the refused copy the item is {Py.Repr(unchanged)}, expected it unchanged from {Py.Repr(original)}");
        Ensure(!unchanged.ContainsKey("c2"), $"a refused SET c2 = b must not create the target attribute c2: {Py.Repr(unchanged)}");

        // Positive control: once b holds a value, the identical copy expression succeeds.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET b = :v",
            ExpressionAttributeValues = Item((":v", N("7"))),
        });
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET c2 = b",
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("c2", N("7")))),
            $"SET c2 = b once b=7 returned {Py.Repr(resp.Attributes)}, expected c2=7");
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final, Item(("pk", S("doc")), ("keep", S("k")), ("b", N("7")), ("c2", N("7")))),
            $"final item is {Py.Repr(final)}, expected keep intact, b=7 and c2=7");
    }
}
