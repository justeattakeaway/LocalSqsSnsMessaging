// Translated from behavioral/dynamodb/check_query_nested_non_redundant_parenthesized_key_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-nested-non-redundant-parenthesized-key-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryNestedNonRedundantParenthesizedKeyCondition
{
    private const string Check = "query-nested-non-redundant-parenthesized-key-condition";

    /// <summary>
    /// A KeyConditionExpression whose parentheses nest around a sub-clause, rather than
    /// wrapping the whole expression once, is accepted and evaluated correctly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-nested-non-redundant-parenthesized-key-condition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 3; i++)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S($"s{i}"))));
        }

        // The inner paren wraps only the sort-key clause; the outer paren wraps the whole
        // AND, so the two parens enclose different, non-identical sub-expressions.
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "(pk = :p AND (sk = :s))",
            ExpressionAttributeValues = Item((":p", S("p1")), (":s", S("s1"))),
            ConsistentRead = true,
        });
        Ensure(resp.Count == 1, $"expected exactly 1 item, got {resp.Count}");
        Ensure(Py.Equal(resp.Items[0]["sk"], S("s1")), $"expected sk=s1, got {Py.Repr(resp.Items[0].GetValueOrDefault("sk"))}");

        // A second nesting shape: the inner paren wraps only the partition-key clause.
        var resp2 = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "((pk = :p) AND sk BETWEEN :lo AND :hi)",
            ExpressionAttributeValues = Item((":p", S("p1")), (":lo", S("s0")), (":hi", S("s1"))),
            ConsistentRead = true,
        });
        var sks = resp2.Items.Select(item => item["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(sks.SequenceEqual(["s0", "s1"]), $"expected sk in [s0, s1], got {Py.Repr(sks)}");
    }
}
