// Translated from behavioral/dynamodb/check_partiql_attribute_exists_on_absent_nested_path_is_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-attribute-exists-on-absent-nested-path-is-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlAttributeExistsOnAbsentNestedPathIsFalse
{
    private const string CheckId = "partiql-attribute-exists-on-absent-nested-path-is-false";

    /// <summary>A pk/sk table for PartiQL SELECT statements.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-pqane-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    /// <summary>Every matching (pk, sk), as a sorted set, from a consistent SELECT.</summary>
    private static async Task<List<(string, string)>> Select(IAmazonDynamoDB ddb, string statement)
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
                return [.. items.Select(i => (i["pk"].S, i["sk"].S)).OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal)];
            }
        }
    }

    private static string TupleRepr(List<(string, string)> values) =>
        "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + (values.Count == 1 ? ",)" : ")");

    /// <summary>
    /// A PartiQL WHERE clause's attribute_exists on a nested path evaluates false whenever that
    /// path is absent, whether its parent map is present with other keys or missing entirely.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-attribute-exists-on-absent-nested-path-is-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await MakeTable(ctx);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("a1")), ("m", M(("sub", S("x"))))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("a2")), ("m", M())));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("a3"))));

        var nestedPresent = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND attribute_exists(m.sub)");
        Ensure(
            nestedPresent.SequenceEqual([("p1", "a1")]),
            $"attribute_exists(m.sub) returned {TupleRepr(nestedPresent)}, expected only the item carrying m.sub");

        var nestedAbsent = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND attribute_exists(m.nope)");
        Ensure(
            nestedAbsent.Count == 0,
            $"attribute_exists(m.nope) returned {TupleRepr(nestedAbsent)}, expected none: m.nope is absent on "
            + "every item, whether m itself is present (empty or non-empty) or missing entirely");
    }
}
