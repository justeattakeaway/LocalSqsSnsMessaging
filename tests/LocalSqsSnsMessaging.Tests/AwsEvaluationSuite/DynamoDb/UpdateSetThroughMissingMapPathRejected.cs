// Translated from behavioral/dynamodb/check_update_set_through_missing_map_path_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-set-through-missing-map-path-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateSetThroughMissingMapPathRejected
{
    private const string Check = "update-set-through-missing-map-path-rejected";
    private const string PathInvalid = "The document path provided in the update expression is invalid for update";
    private static readonly TimeSpan TableActiveBudget = TimeSpan.FromSeconds(120.0);
    private static readonly TimeSpan TeardownWait = TimeSpan.FromSeconds(120.0);
    private static readonly string[] Expressions = ["SET m.a.b = :v", "SET m.a = :v"];

    /// <summary>
    /// SET on a nested path whose parent map does not exist fails with ValidationException and
    /// writes nothing (no auto-created maps); once the map exists the same SET succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-set-through-missing-map-path-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        await ddb.PutItemAsync(table, Item(("pk", S("doc")), ("keep", S("k"))));

        foreach (var expr in Expressions)
        {
            var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = expr,
                ExpressionAttributeValues = Item((":v", S("deep"))),
            }));
            Ensure(
                code == "ValidationException",
                $"{Py.Repr(expr)} with no stored map m answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
                "ValidationException");
            Ensure(
                message == PathInvalid,
                $"{Py.Repr(expr)} refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");
        }

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("doc")), ("keep", S("k")))),
            $"after two refused nested SETs the item is {Py.Repr(item)}; a refused update must not " +
            "auto-create the intermediate map");

        // Positive control: create the parent, then the identical one-level SET is legal.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET m = :empty",
            ExpressionAttributeValues = Item((":empty", M())),
        });
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET m.a = :v",
            ExpressionAttributeValues = Item((":v", S("deep"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("m", M(("a", S("deep")))))),
            $"SET m.a once m exists returned {Py.Repr(resp.Attributes)}, expected m={{a: deep}}");
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final, Item(("pk", S("doc")), ("keep", S("k")), ("m", M(("a", S("deep")))))),
            $"final item {Py.Repr(final)}, expected keep intact and m={{a: deep}}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<UpdateItemResponse>> fn)
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"call unexpectedly SUCCEEDED where an error was required: {Py.Repr(resp?.Attributes)}");
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

            if (error.Code == "ResourceInUseException" && clock.Elapsed < TeardownWait)
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
        TableStatus? status = null;
        while (clock.Elapsed < TableActiveBudget)
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within 120.0s: {Py.Repr(status)}");
    }
}
