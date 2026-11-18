// Translated from behavioral/dynamodb/check_partiql_or_negated_sort_key_branch_runs_and_unions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-or-negated-sort-key-branch-runs-and-unions</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlOrNegatedSortKeyBranchRunsAndUnions
{
    private const string CheckId = "partiql-or-negated-sort-key-branch-runs-and-unions";
    private static readonly string[] SortKeys = ["1", "2", "3"];

    private static Dictionary<string, AttributeValue> MakeItem(string pk, string sk) => Item(("pk", S(pk)), ("sk", S(sk)));

    /// <summary>Every item the statement returns, following NextToken.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> SelectAll(IAmazonDynamoDB ddb, string statement)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        string? token = null;
        while (true)
        {
            var request = new ExecuteStatementRequest { Statement = statement, ConsistentRead = true };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await ddb.ExecuteStatementAsync(request);
            items.AddRange(page.Items);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return items;
            }
        }
    }

    private static List<string> SkSet(List<Dictionary<string, AttributeValue>> items) =>
        items.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// A WHERE clause ORing a negated sort-key branch beside another sort-key branch on the
    /// same partition key runs and returns the union, for every documented negated form.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-or-negated-sort-key-branch-runs-and-unions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlor-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        foreach (var sk in SortKeys)
        {
            await ddb.PutItemAsync(table, MakeItem("p", sk));
        }

        // A sibling partition that would match every branch below, to prove no cross-partition leak.
        await ddb.PutItemAsync(table, MakeItem("q", "1"));
        await ddb.PutItemAsync(table, MakeItem("q", "2"));

        async Task<List<string>> Select(string where) => SkSet(await SelectAll(ddb, $"SELECT * FROM \"{table}\" WHERE {where}"));

        // Positive control: a plain pk+sk equality still runs and returns exactly that one item,
        // proving the table and the query path work before any OR/NOT form is exercised.
        var plain = await Select("pk = 'p' AND sk = '2'");
        Ensure(plain.SequenceEqual(["2"]), $"pk = 'p' AND sk = '2' must return exactly sk 2, got {Py.Repr(plain)}");

        // NOT sk = <literal> OR sk = <literal>: the negated branch admits 2 and 3, the plain branch
        // re-admits 2; the union is {2, 3}.
        var got = await Select("pk = 'p' AND NOT sk = '1' OR pk = 'p' AND sk = '2'");
        Ensure(
            got.SequenceEqual(["2", "3"]),
            $"pk = 'p' AND NOT sk = '1' OR pk = 'p' AND sk = '2' must return the union {{2, 3}}, got {Py.Repr(got)}");

        // The same claim written with NOT wrapping a parenthesised equality must parse and answer
        // identically to the unparenthesised NOT above.
        var parenthesised = await Select("pk = 'p' AND NOT (sk = '1') OR pk = 'p' AND sk = '2'");
        Ensure(
            parenthesised.SequenceEqual(["2", "3"]),
            $"pk = 'p' AND NOT (sk = '1') OR pk = 'p' AND sk = '2' must return the union {{2, 3}}, got {Py.Repr(parenthesised)}");

        // sk <> <literal> is itself a negated comparison; it must union the same way as NOT sk =.
        var notEqual = await Select("pk = 'p' AND sk <> '1' OR pk = 'p' AND sk = '2'");
        Ensure(
            notEqual.SequenceEqual(["2", "3"]),
            $"pk = 'p' AND sk <> '1' OR pk = 'p' AND sk = '2' must return the union {{2, 3}}, got {Py.Repr(notEqual)}");

        // NOT begins_with: the sort keys are single digits, so begins_with(sk, '1') matches only '1'
        // and its negation admits 2 and 3 — the same union as the equality forms above.
        var notBegins = await Select("pk = 'p' AND NOT begins_with(sk, '1') OR pk = 'p' AND sk = '2'");
        Ensure(
            notBegins.SequenceEqual(["2", "3"]),
            $"pk = 'p' AND NOT begins_with(sk, '1') OR pk = 'p' AND sk = '2' must return the union {{2, 3}}, got {Py.Repr(notBegins)}");

        // NOT sk IN (...): excluding two of the three keys leaves one from the negated branch, and
        // the plain branch re-admits that same key, so the union collapses to a single element —
        // distinct from the single-literal negations above, proving IN's list is honoured.
        var notIn = await Select("pk = 'p' AND NOT sk IN ['1', '3'] OR pk = 'p' AND sk = '2'");
        Ensure(
            notIn.SequenceEqual(["2"]),
            $"pk = 'p' AND NOT sk IN ['1', '3'] OR pk = 'p' AND sk = '2' must return just {{2}}, got {Py.Repr(notIn)}");

        // NOT sk > <literal>: admits keys at or below the literal (here just '1'); ORed with the
        // plain sk = '2' branch the union is {1, 2}, disjoint from the equality-negation unions above.
        var notGt = await Select("pk = 'p' AND NOT sk > '1' OR pk = 'p' AND sk = '2'");
        Ensure(
            notGt.SequenceEqual(["1", "2"]),
            $"pk = 'p' AND NOT sk > '1' OR pk = 'p' AND sk = '2' must return the union {{1, 2}}, got {Py.Repr(notGt)}");
    }
}
