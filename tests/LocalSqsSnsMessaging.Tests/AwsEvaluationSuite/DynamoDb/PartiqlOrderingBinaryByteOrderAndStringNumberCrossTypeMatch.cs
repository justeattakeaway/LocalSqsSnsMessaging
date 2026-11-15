// Translated from behavioral/dynamodb/check_partiql_ordering_binary_byte_order_and_string_number_cross_type_match.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-ordering-binary-byte-order-and-string-number-cross-type-match</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlOrderingBinaryByteOrderAndStringNumberCrossTypeMatch
{
    private const string CheckId = "partiql-ordering-binary-byte-order-and-string-number-cross-type-match";

    private static async Task<(AmazonDynamoDBClient Ddb, string Name)> Table(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-pqlord-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return (ddb, name);
    }

    /// <summary>
    /// A PartiQL ordering comparator against a parameterized Binary value orders by unsigned
    /// byte value (0x01 &lt; 0x80), and the same comparator between a String attribute and a Number
    /// parameter is accepted and matches no item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-ordering-binary-byte-order-and-string-number-cross-type-match")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var (ddb, table) = await Table(ctx);

        var item = Item(("pk", S("row")), ("bin", B([0x01])), ("strv", S("abc")));
        await ddb.PutItemAsync(table, item);

        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("row"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(stored, item), $"item changed on the way back from PutItem/GetItem: {Py.Repr(stored)}");

        // Positive control: a parameterized Binary equality comparison finds the item, so the
        // mechanism for binding a Binary parameter into a WHERE clause works before ordering is tried.
        var eq = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ? AND bin = ?",
            Parameters = [S("row"), B([0x01])],
            ConsistentRead = true,
        });
        Ensure(Py.Equal(eq.Items, new[] { item }), $"bin = B'\\x01' must find the stored item, got {Py.Repr(eq.Items)}");

        var less = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ? AND bin < ?",
            Parameters = [S("row"), B([0x80])],
            ConsistentRead = true,
        });
        Ensure(
            Py.Equal(less.Items, new[] { item }),
            $"bin < B'\\x80' must match the stored byte 0x01 by unsigned byte order, got {Py.Repr(less.Items)}");

        var greater = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ? AND bin > ?",
            Parameters = [S("row"), B([0x80])],
            ConsistentRead = true,
        });
        Ensure(
            greater.Items.Count == 0,
            $"bin > B'\\x80' must not match the stored byte 0x01 by unsigned byte order, got {Py.Repr(greater.Items)}");

        // Positive control: a same-type (String) ordering comparison matches, so the empty result
        // below is attributable to the cross-type comparison, not to '<' being broken for strv.
        var sameType = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ? AND strv < ?",
            Parameters = [S("row"), S("xyz")],
            ConsistentRead = true,
        });
        Ensure(Py.Equal(sameType.Items, new[] { item }), $"strv < 'xyz' must match the stored item, got {Py.Repr(sameType.Items)}");

        var crossType = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ? AND strv < ?",
            Parameters = [S("row"), N("5")],
            ConsistentRead = true,
        });
        Ensure(crossType.Items.Count == 0, $"strv < N'5' must be accepted and match nothing; got {Py.Repr(crossType.Items)}");
    }
}
