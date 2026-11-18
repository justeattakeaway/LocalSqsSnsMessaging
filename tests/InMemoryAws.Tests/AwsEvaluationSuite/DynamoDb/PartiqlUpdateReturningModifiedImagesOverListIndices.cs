// Translated from behavioral/dynamodb/check_partiql_update_returning_modified_images_over_list_indices.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-returning-modified-images-over-list-indices</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateReturningModifiedImagesOverListIndices
{
    private const string CheckId = "partiql-update-returning-modified-images-over-list-indices";

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) =>
        async () =>
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is not null && !notFound.Contains(error.Code))
            {
                throw error.Exception;
            }
        };

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    private static Task Reset(IAmazonDynamoDB ddb, string table, string pk, params string[] tagsList) =>
        ddb.PutItemAsync(table, Item(("pk", S(pk)), ("tags", L([.. tagsList.Select(S)]))));

    /// <summary>
    /// UPDATE RETURNING MODIFIED * over list indices: several SET indices return a dense,
    /// ascending list regardless of SET order; SET at the list length appends (NEW carries it, OLD
    /// is empty); REMOVE of an index returns it under OLD and the shifted-in element under NEW.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-returning-modified-images-over-list-indices")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqllistidx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        async Task<List<Dictionary<string, AttributeValue>>> Execute(string statement, params AttributeValue[] parameters) =>
            (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [.. parameters] })).Items;

        async Task<AttributeValue> StoredTags() => (await Stored(ddb, table, "r1"))!["tags"];

        // 1. A single non-zero index: MODIFIED NEW * carries only that element.
        await Reset(ddb, table, "r1", "a", "b", "c");
        var r = await Execute($"UPDATE \"{table}\" SET tags[2] = ? WHERE pk = ? RETURNING MODIFIED NEW *", S("y"), S("r1"));
        Ensure(Py.Equal(r, new[] { Item(("tags", L(S("y")))) }), $"SET tags[2] MODIFIED NEW * returned {Py.Repr(r)}");
        Ensure(Py.Equal(await StoredTags(), L(S("a"), S("b"), S("y"))), $"stored tags after SET tags[2] is {Py.Repr(await StoredTags())}");

        // 2. Several indices, SET in descending index order: MODIFIED NEW * is still dense and
        // ascending by index, not in SET order.
        await Reset(ddb, table, "r1", "a", "b", "c");
        r = await Execute($"UPDATE \"{table}\" SET tags[2] = ? SET tags[0] = ? WHERE pk = ? RETURNING MODIFIED NEW *", S("y"), S("x"), S("r1"));
        Ensure(
            Py.Equal(r, new[] { Item(("tags", L(S("x"), S("y")))) }),
            $"SET tags[2] then tags[0] MODIFIED NEW * returned {Py.Repr(r)}, expected ascending index order");
        Ensure(Py.Equal(await StoredTags(), L(S("x"), S("b"), S("y"))), $"stored tags after the two SETs is {Py.Repr(await StoredTags())}");

        // 3. SET at exactly the list length appends: NEW returns the appended element, OLD returns [].
        await Reset(ddb, table, "r1", "a", "b", "c");
        var rNew = await Execute($"UPDATE \"{table}\" SET tags[3] = ? WHERE pk = ? RETURNING MODIFIED NEW *", S("d"), S("r1"));
        Ensure(Py.Equal(rNew, new[] { Item(("tags", L(S("d")))) }), $"SET tags[3] (append) MODIFIED NEW * returned {Py.Repr(rNew)}");
        var rOld = await Execute($"UPDATE \"{table}\" SET tags[4] = ? WHERE pk = ? RETURNING MODIFIED OLD *", S("e"), S("r1"));
        Ensure(rOld.Count == 0, $"SET tags[4] (append) MODIFIED OLD * returned {Py.Repr(rOld)}, expected []");
        Ensure(
            Py.Equal(await StoredTags(), L(S("a"), S("b"), S("c"), S("d"), S("e"))),
            $"stored tags after the two appends is {Py.Repr(await StoredTags())}");

        // 4. REMOVE tags[1] returns the removed element under OLD and the shifted-in element under NEW.
        await Reset(ddb, table, "r1", "a", "b", "c");
        rOld = await Execute($"UPDATE \"{table}\" REMOVE tags[1] WHERE pk = ? RETURNING MODIFIED OLD *", S("r1"));
        Ensure(Py.Equal(rOld, new[] { Item(("tags", L(S("b")))) }), $"REMOVE tags[1] MODIFIED OLD * returned {Py.Repr(rOld)}");
        Ensure(Py.Equal(await StoredTags(), L(S("a"), S("c"))), $"stored tags after REMOVE tags[1] is {Py.Repr(await StoredTags())}");

        await Reset(ddb, table, "r1", "a", "b", "c");
        rNew = await Execute($"UPDATE \"{table}\" REMOVE tags[1] WHERE pk = ? RETURNING MODIFIED NEW *", S("r1"));
        Ensure(
            Py.Equal(rNew, new[] { Item(("tags", L(S("c")))) }),
            $"REMOVE tags[1] MODIFIED NEW * returned {Py.Repr(rNew)}, expected the shifted-in element");

        // 5. REMOVE of the last index returns Items [] under NEW, because there is no element left
        // at that position to report.
        await Reset(ddb, table, "r1", "a", "b");
        rNew = await Execute($"UPDATE \"{table}\" REMOVE tags[1] WHERE pk = ? RETURNING MODIFIED NEW *", S("r1"));
        Ensure(rNew.Count == 0, $"REMOVE of the last index MODIFIED NEW * returned {Py.Repr(rNew)}, expected []");
        Ensure(Py.Equal(await StoredTags(), L(S("a"))), $"stored tags after removing the last index is {Py.Repr(await StoredTags())}");
    }
}
