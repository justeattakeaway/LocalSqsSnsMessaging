// Translated from behavioral/dynamodb/check_partiql_update_returning_modified_old_images_over_list_indices.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-returning-modified-old-images-over-list-indices</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateReturningModifiedOldImagesOverListIndices
{
    private const string CheckId = "partiql-update-returning-modified-old-images-over-list-indices";

    private static async Task<Dictionary<string, AttributeValue>> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    private static Task Reset(IAmazonDynamoDB ddb, string table, string pk, params string[] tagsList) =>
        ddb.PutItemAsync(table, Item(("pk", S(pk)), ("tags", L([.. tagsList.Select(S)]))));

    private static List<Dictionary<string, AttributeValue>> Tags(params string[] values) => [Item(("tags", L([.. values.Select(S)])))];

    /// <summary>
    /// UPDATE RETURNING MODIFIED OLD * over list indices: REMOVE of the last index returns the
    /// removed element; several SET indices return their prior values, dense and in ascending
    /// index order regardless of SET order.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.update.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-returning-modified-old-images-over-list-indices")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqllistidxold-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        // 1. REMOVE of the LAST index: MODIFIED OLD * returns the removed element itself.
        await Reset(ddb, table, "r1", "a", "b", "c");
        var rOld = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" REMOVE tags[2] WHERE pk = ? RETURNING MODIFIED OLD *",
            Parameters = [S("r1")],
        })).Items;
        Ensure(Py.Equal(rOld, Tags("c")), $"REMOVE of the last index MODIFIED OLD * returned {Py.Repr(rOld)}, expected the removed element");
        var stored = (await Stored(ddb, table, "r1"))["tags"];
        Ensure(Py.Equal(stored, L(S("a"), S("b"))), $"stored tags after removing the last index is {Py.Repr(stored)}");

        // 2. Several indices, SET in descending index order: MODIFIED OLD * returns the PRIOR values,
        // dense and in ascending index order, not SET order.
        await Reset(ddb, table, "r1", "a", "b", "c");
        rOld = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET tags[2] = ? SET tags[0] = ? WHERE pk = ? RETURNING MODIFIED OLD *",
            Parameters = [S("y"), S("x"), S("r1")],
        })).Items;
        Ensure(
            Py.Equal(rOld, Tags("a", "c")),
            $"SET tags[2] then tags[0] MODIFIED OLD * returned {Py.Repr(rOld)}, expected prior values in ascending index order");
        stored = (await Stored(ddb, table, "r1"))["tags"];
        Ensure(Py.Equal(stored, L(S("x"), S("b"), S("y"))), $"stored tags after the two SETs is {Py.Repr(stored)}");

        // 3. Three non-contiguous indices SET out of order: MODIFIED OLD * still packs the prior
        // values densely and in ascending index order.
        await Reset(ddb, table, "r2", "a", "b", "c", "d");
        rOld = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET tags[3] = ? SET tags[0] = ? SET tags[1] = ? "
                + "WHERE pk = ? RETURNING MODIFIED OLD *",
            Parameters = [S("w"), S("x"), S("y"), S("r2")],
        })).Items;
        Ensure(
            Py.Equal(rOld, Tags("a", "b", "d")),
            $"SET tags[3], tags[0], tags[1] MODIFIED OLD * returned {Py.Repr(rOld)}, expected the three prior values in ascending index order");
        stored = (await Stored(ddb, table, "r2"))["tags"];
        Ensure(Py.Equal(stored, L(S("x"), S("y"), S("c"), S("w"))), $"stored tags after the three SETs is {Py.Repr(stored)}");
    }
}
