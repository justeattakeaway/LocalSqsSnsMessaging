// Translated from behavioral/dynamodb/check_execute_statement_number_literal_exponent_and_leading_dot_forms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-number-literal-exponent-and-leading-dot-forms</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementNumberLiteralExponentAndLeadingDotForms
{
    private const string CheckId = "execute-statement-number-literal-exponent-and-leading-dot-forms";
    private static readonly string[] BaseLiterals = ["7.31e6", "7.31E6", "7.31e+6", "731e4", "73100000e-1"];

    /// <summary>
    /// ExecuteStatement number literals accept exponent and leading-dot forms and compare equal
    /// to the canonical N they denote; an INSERTed literal of either form stores that canonical N.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.insert.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-number-literal-exponent-and-leading-dot-forms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 30);

        // A canonical N 7310000, and two signed canonical Ns (7.3 and -7.3) that the leading-dot
        // exponent forms below must resolve to, each written with an unadorned decimal literal.
        await Insert(ddb, tableName, "base", "7310000");
        await Insert(ddb, tableName, "pos", "7.3");
        await Insert(ddb, tableName, "neg", "-7.3");

        foreach (var literal in BaseLiterals)
        {
            var item = await SelectOne(ddb, tableName, literal);
            Ensure(Py.Equal(item["pk"], S("base")), $"literal {Py.Repr(literal)} matched pk {Py.Repr(item["pk"])}, expected the 7310000 item");
            Ensure(Py.Equal(item["n"], N("7310000")), $"literal {Py.Repr(literal)} matched an n of {Py.Repr(item["n"])}, expected the canonical N 7310000");
        }

        var posItem = await SelectOne(ddb, tableName, ".73e1");
        Ensure(Py.Equal(posItem["pk"], S("pos")), $"literal '.73e1' matched pk {Py.Repr(posItem["pk"])}, expected the 7.3 item");
        Ensure(Py.Equal(posItem["n"], N("7.3")), $"literal '.73e1' matched n {Py.Repr(posItem["n"])}, expected N 7.3");

        var negItem = await SelectOne(ddb, tableName, "-.73e1");
        Ensure(Py.Equal(negItem["pk"], S("neg")), $"literal '-.73e1' matched pk {Py.Repr(negItem["pk"])}, expected the -7.3 item");
        Ensure(Py.Equal(negItem["n"], N("-7.3")), $"literal '-.73e1' matched n {Py.Repr(negItem["n"])}, expected N -7.3");

        // An INSERTed exponent or leading-dot literal is itself canonicalized before it is stored.
        await Insert(ddb, tableName, "ins1", "7.32e6");
        var stored1 = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("ins1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(stored1["n"], N("7320000")), $"INSERT of 7.32e6 stored n as {Py.Repr(stored1["n"])}, expected the canonical N 7320000");

        await Insert(ddb, tableName, "ins2", ".7325e1");
        var stored2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("ins2"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(stored2["n"], N("7.325")), $"INSERT of .7325e1 stored n as {Py.Repr(stored2["n"])}, expected the canonical N 7.325");
    }

    private static Task Insert(IAmazonDynamoDB ddb, string tableName, string pk, string nLiteral) =>
        ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': '{pk}', 'n': {nLiteral}}}" });

    private static async Task<Dictionary<string, AttributeValue>> SelectOne(IAmazonDynamoDB ddb, string tableName, string nLiteral)
    {
        var items = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{tableName}\" WHERE n = {nLiteral}",
            ConsistentRead = true,
        })).Items;
        Ensure(items.Count == 1, $"literal {Py.Repr(nLiteral)} matched {items.Count} items, expected exactly 1");
        return items[0];
    }
}
