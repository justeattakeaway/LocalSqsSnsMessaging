// Translated from behavioral/dynamodb/check_update_item_add_delete_on_map_attribute_and_nested_arithmetic_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-delete-on-map-attribute-and-nested-arithmetic-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddDeleteOnMapAttributeAndNestedArithmeticTypeMismatchRejected
{
    private const string Check = "update-item-add-delete-on-map-attribute-and-nested-arithmetic-type-mismatch-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;
    private const string TypeMismatch = "An operand in the update expression has an incorrect data type";

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (resp, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where an error was required: {resp}");
        }

        return (err.Code, err.Message);
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
    /// ADD and DELETE on a Map-typed attribute, and SET arithmetic on a nested leaf of the
    /// wrong type, are each rejected with ValidationException and leave the item unchanged; the
    /// same actions against same-typed siblings still apply.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-delete-on-map-attribute-and-nested-arithmetic-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(
            ("pk", S("doc")),
            ("cfg", M(("region", S("us-west-2")))),
            ("hits", N("10")),
            ("labels", SS("a", "b")),
            ("nested", M(("stat", M(("score", S("not-a-number")), ("visits", N("1")))))));
        await ddb.PutItemAsync(table, original);

        var cfgNames = Names(("#a", "cfg"));
        var scoreNames = Names(("#n", "nested"), ("#t", "stat"), ("#s", "score"));
        var visitsNames = Names(("#n", "nested"), ("#t", "stat"), ("#v", "visits"));

        // ADD onto a Map-typed top-level attribute: ADD only accepts Number or Set operands.
        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD #a :one",
            ExpressionAttributeNames = cfgNames,
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            $"ADD cfg :one with cfg stored as M answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == TypeMismatch, $"ADD cfg :one refusal message is {Py.Repr(message)}, expected {Py.Repr(TypeMismatch)}");

        // DELETE onto a Map-typed top-level attribute: DELETE only accepts Set operands against a Set.
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE #a :v",
            ExpressionAttributeNames = cfgNames,
            ExpressionAttributeValues = Item((":v", SS("x"))),
        }));
        Ensure(
            code == "ValidationException",
            $"DELETE cfg :v with cfg stored as M answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == TypeMismatch, $"DELETE cfg :v refusal message is {Py.Repr(message)}, expected {Py.Repr(TypeMismatch)}");

        // SET arithmetic on a nested leaf (nested.stat.score) stored as a String.
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #n.#t.#s = #n.#t.#s + :one",
            ExpressionAttributeNames = scoreNames,
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            "SET nested.stat.score = nested.stat.score + :one with score stored as S answered " +
            $"{Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == TypeMismatch, $"nested arithmetic refusal message is {Py.Repr(message)}, expected {Py.Repr(TypeMismatch)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, original),
            $"after the three refused type-mismatched updates the item is {Py.Repr(unchanged)}, expected it " +
            $"unchanged from {Py.Repr(original)}");

        // Positive controls: the identical action against a same-typed sibling applies.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD hits :delta",
            ExpressionAttributeValues = Item((":delta", N("5"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("hits", N("15")))),
            $"ADD hits :delta on a Number attribute returned {Py.Repr(resp.Attributes)}, expected hits=15");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE labels :v",
            ExpressionAttributeValues = Item((":v", SS("a"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("labels", SS("b")))),
            $"DELETE labels :v on a String Set attribute returned {Py.Repr(resp.Attributes)}, expected labels={{b}}");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #n.#t.#v = #n.#t.#v + :one",
            ExpressionAttributeNames = visitsNames,
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("nested", M(("stat", M(("visits", N("2")))))))),
            $"nested SET arithmetic on a Number leaf returned {Py.Repr(resp.Attributes)}, expected visits=2");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(final["cfg"], M(("region", S("us-west-2")))), $"cfg must still read its original value: {Py.Repr(final["cfg"])}");
        Ensure(
            Py.Equal(final["nested"].M["stat"].M["score"], S("not-a-number")),
            $"nested.stat.score must still read its original value: {Py.Repr(final["nested"])}");
        Ensure(
            Py.Equal(final["hits"], N("15")) && final["labels"].SS.Distinct().SequenceEqual(["b"])
            && Py.Equal(final["nested"].M["stat"].M["visits"], N("2")),
            $"all three positive controls must have landed: {Py.Repr(final)}");
    }
}
