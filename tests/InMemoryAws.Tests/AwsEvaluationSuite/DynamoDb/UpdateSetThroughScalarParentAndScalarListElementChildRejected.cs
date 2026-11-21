// Translated from behavioral/dynamodb/check_update_set_through_scalar_parent_and_scalar_list_element_child_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-set-through-scalar-parent-and-scalar-list-element-child-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateSetThroughScalarParentAndScalarListElementChildRejected
{
    private const string Check = "update-set-through-scalar-parent-and-scalar-list-element-child-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;
    private const string PathInvalid = "The document path provided in the update expression is invalid for update";

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
    /// SET rejects a path through a scalar attribute and through a scalar list element, leaving
    /// the item unchanged, while the same SET form against a real map or a map-valued element succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-set-through-scalar-parent-and-scalar-list-element-child-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(
            ("pk", S("doc")),
            ("word", S("scalar")),
            ("tags", L(S("a"), S("b"))),
            ("box", M()),
            ("entries", L(M())));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        UpdateItemRequest SetChild(string expression) => new()
        {
            TableName = table,
            Key = key,
            UpdateExpression = expression,
            ExpressionAttributeValues = Item((":v", S("x"))),
        };

        // SET through a scalar top-level parent ("word" is a String, not a map).
        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(SetChild("SET word.child = :v")));
        Ensure(
            code == "ValidationException",
            $"SET word.child with word stored as S answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            "ValidationException");
        Ensure(message == PathInvalid, $"SET word.child refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        // SET through a scalar list element ("tags[0]" is a String, not a map).
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(SetChild("SET tags[0].child = :v")));
        Ensure(
            code == "ValidationException",
            $"SET tags[0].child with tags[0] stored as S answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            "ValidationException");
        Ensure(message == PathInvalid, $"SET tags[0].child refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        var unchanged = await Read();
        Ensure(
            Py.Equal(unchanged, original),
            $"after the two refused SETs the item is {Py.Repr(unchanged)}, expected it unchanged from " +
            $"{Py.Repr(original)}");

        // Positive controls: the identical SET forms against a real map and a map-valued list element.
        await ddb.UpdateItemAsync(SetChild("SET box.child = :v"));
        var afterBox = await Read();
        Ensure(
            Py.Equal(afterBox["box"], M(("child", S("x")))),
            $"SET box.child on a real map produced {Py.Repr(afterBox["box"])}, expected child='x'");

        await ddb.UpdateItemAsync(SetChild("SET entries[0].child = :v"));
        var final = await Read();
        Ensure(
            Py.Equal(final["entries"], L(M(("child", S("x"))))),
            $"SET entries[0].child on a map-valued list element produced {Py.Repr(final["entries"])}, " +
            "expected child='x'");
        Ensure(
            Py.Equal(final["word"], S("scalar")) && Py.Equal(final["tags"], original["tags"]),
            $"the attributes under test must still read their original values: {Py.Repr(final)}");
    }
}
