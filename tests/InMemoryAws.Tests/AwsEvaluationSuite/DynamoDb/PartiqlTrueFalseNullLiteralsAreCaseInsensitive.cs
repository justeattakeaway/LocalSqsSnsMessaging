// Translated from behavioral/dynamodb/check_partiql_true_false_null_literals_are_case_insensitive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-true-false-null-literals-are-case-insensitive</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlTrueFalseNullLiteralsAreCaseInsensitive
{
    private const string CheckId = "partiql-true-false-null-literals-are-case-insensitive";

    /// <summary>Bound on the table's CREATING -> ACTIVE transition.</summary>
    private const double WaitS = 40.0;

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = WaitS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            string status = (await ddb.DescribeTableAsync(table)).Table.TableStatus;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutS.ToString("F0", CultureInfo.InvariantCulture)}s; last status {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    private static async Task<HashSet<string>> SelectPks(IAmazonDynamoDB ddb, string table, string where)
    {
        var items = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE {where}",
            ConsistentRead = true,
        })).Items;
        return items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// PartiQL INSERT stores the true/False/TRUE and null/Null/NULL literals as BOOL and NULL
    /// regardless of their case, and a WHERE comparison written in a different case still matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-true-false-null-literals-are-case-insensitive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlit-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableActive(ddb, table);

        // Each pk's INSERT spells its literal in a different case; GetItem proves the stored type.
        (string Pk, string Literal, AttributeValue Expected)[] boolItems =
        [
            ("flag_true_lower", "true", Bool(true)),
            ("flag_false_mixed", "False", Bool(false)),
            ("flag_true_upper", "TRUE", Bool(true)),
        ];
        (string Pk, string Literal)[] nullItems =
        [
            ("val_null_lower", "null"),
            ("val_null_mixed", "Null"),
            ("val_null_upper", "NULL"),
        ];

        foreach (var (pk, literal, _) in boolItems)
        {
            await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': '{pk}', 'flag': {literal}}}",
            });
        }

        foreach (var (pk, literal) in nullItems)
        {
            await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': '{pk}', 'val': {literal}}}",
            });
        }

        foreach (var (pk, literal, expected) in boolItems)
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got["flag"], expected), $"INSERT with 'flag': {literal} stored flag={Py.Repr(got["flag"])}, expected {Py.Repr(expected)}");
        }

        foreach (var (pk, literal) in nullItems)
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got["val"], Null()), $"INSERT with 'val': {literal} stored val={Py.Repr(got["val"])}, expected {{'NULL': True}}");
        }

        var trueMatched = await SelectPks(ddb, table, "flag = TRUE");
        Ensure(
            trueMatched.SetEquals(["flag_true_lower", "flag_true_upper"]),
            $"WHERE flag = TRUE returned {Py.Repr(trueMatched.Order(StringComparer.Ordinal).ToList())}, expected the two items stored BOOL true");

        var falseMatched = await SelectPks(ddb, table, "flag = false");
        Ensure(
            falseMatched.SetEquals(["flag_false_mixed"]),
            $"WHERE flag = false returned {Py.Repr(falseMatched.Order(StringComparer.Ordinal).ToList())}, expected only the item stored BOOL false");
    }
}
