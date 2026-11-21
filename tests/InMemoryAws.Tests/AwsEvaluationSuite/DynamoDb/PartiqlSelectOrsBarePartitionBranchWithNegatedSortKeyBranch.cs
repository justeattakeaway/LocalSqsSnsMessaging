// Translated from behavioral/dynamodb/check_partiql_select_ors_bare_partition_branch_with_negated_sort_key_branch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-ors-bare-partition-branch-with-negated-sort-key-branch</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectOrsBarePartitionBranchWithNegatedSortKeyBranch
{
    private const string CheckId = "partiql-select-ors-bare-partition-branch-with-negated-sort-key-branch";

    private static Dictionary<string, AttributeValue> PkSk(string pk, string sk) => Item(("pk", S(pk)), ("sk", S(sk)));

    /// <summary>Every item the statement returns, following NextToken.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> SelectAll(IAmazonDynamoDB ddb, string statement)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        string? token = null;
        while (true)
        {
            var page = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, ConsistentRead = true, NextToken = token });
            items.AddRange(page.Items);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return items;
            }
        }
    }

    private static List<(string, string)> Ids(List<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(i => (i["pk"].S, i["sk"].S)).OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal)];

    /// <summary>
    /// A WHERE clause ORing a bare partition-key branch with a negated sort-key branch on a
    /// different partition runs and returns their union, regardless of the branches' order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-ors-bare-partition-branch-with-negated-sort-key-branch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlbareor-");
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

        foreach (var pk in new[] { "p", "q" })
        {
            foreach (var sk in new[] { "1", "2", "3" })
            {
                await ddb.PutItemAsync(table, PkSk(pk, sk));
            }
        }

        // A sibling partition neither OR branch names, to prove no cross-partition leak.
        await ddb.PutItemAsync(table, PkSk("r", "1"));

        async Task<List<(string, string)>> Select(string where) => Ids(await SelectAll(ddb, $"SELECT * FROM \"{table}\" WHERE {where}"));

        // Positive control: the bare partition branch alone returns the whole partition.
        var bare = await Select("pk = 'p'");
        Ensure(bare.SequenceEqual([("p", "1"), ("p", "2"), ("p", "3")]), $"pk = 'p' alone must return all of partition p, got {Py.Repr(bare)}");

        // Positive control: the negated sort-key branch alone returns the complement on its partition.
        var negated = await Select("pk = 'q' AND NOT sk = '2'");
        Ensure(
            negated.SequenceEqual([("q", "1"), ("q", "3")]),
            $"pk = 'q' AND NOT sk = '2' alone must return q's complement of sk 2, got {Py.Repr(negated)}");

        List<(string, string)> expected = [("p", "1"), ("p", "2"), ("p", "3"), ("q", "1"), ("q", "3")];

        // The bare branch ORed with the negated branch runs and returns their union: all of p, plus
        // q's complement of sk 2 -- q's sk 2 and partition r stay excluded.
        var union = await Select("pk = 'p' OR pk = 'q' AND NOT sk = '2'");
        Ensure(
            union.SequenceEqual(expected),
            $"pk = 'p' OR pk = 'q' AND NOT sk = '2' must return the union {Py.Repr(expected)}, got {Py.Repr(union)}");

        // The same OR, branches swapped, must answer identically: OR is commutative here.
        var swapped = await Select("pk = 'q' AND NOT sk = '2' OR pk = 'p'");
        Ensure(
            swapped.SequenceEqual(expected),
            $"pk = 'q' AND NOT sk = '2' OR pk = 'p' must return the same union {Py.Repr(expected)}, got {Py.Repr(swapped)}");
    }
}
