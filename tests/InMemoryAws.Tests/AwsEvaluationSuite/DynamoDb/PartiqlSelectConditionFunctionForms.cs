// Translated from behavioral/dynamodb/check_partiql_select_condition_function_forms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-condition-function-forms</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectConditionFunctionForms
{
    private const string CheckId = "partiql-select-condition-function-forms";

    /// <summary>A pk/sk table for PartiQL SELECT statements.</summary>
    private static async Task<string> Table(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-pqlcf-");
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
                return items.Select(i => (i["pk"].S, i["sk"].S))
                    .OrderBy(t => t.Item1, StringComparer.Ordinal).ThenBy(t => t.Item2, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>
    /// A PartiQL SELECT WHERE clause evaluates NOT, an upper-case function name, a boolean
    /// comparison, a double-quoted attribute name, a nested path and an OR of functions exactly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-condition-function-forms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("a1")), ("m", M(("sub", S("x")))), ("ss", SS("x", "y"))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("a2")), ("m", M(("sub", S("y"))))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("b1"))));

        var notExists = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND NOT attribute_exists(m)");
        Ensure(notExists.SequenceEqual([("p1", "b1")]), $"NOT attribute_exists(m) returned {Py.Repr(notExists)}, expected the one item lacking m");

        var notBeginsWith = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND NOT begins_with(\"sk\", 'a')");
        Ensure(notBeginsWith.SequenceEqual([("p1", "b1")]), $"NOT begins_with(sk, 'a') returned {Py.Repr(notBeginsWith)}, expected the one sk not starting 'a'");

        var upperExists = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND ATTRIBUTE_EXISTS(m)");
        Ensure(upperExists.SequenceEqual([("p1", "a1"), ("p1", "a2")]), $"upper-case ATTRIBUTE_EXISTS(m) returned {Py.Repr(upperExists)}, expected the two items carrying m");

        var boolCmp = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND attribute_exists(m) = true");
        Ensure(boolCmp.SequenceEqual([("p1", "a1"), ("p1", "a2")]), $"attribute_exists(m) = true returned {Py.Repr(boolCmp)}, expected the two items carrying m");

        var quotedName = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND attribute_exists(\"ss\")");
        Ensure(quotedName.SequenceEqual([("p1", "a1")]), $"attribute_exists(\"ss\") returned {Py.Repr(quotedName)}, expected only the item carrying ss");

        var nestedPresent = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND attribute_exists(m.sub)");
        Ensure(nestedPresent.SequenceEqual([("p1", "a1"), ("p1", "a2")]), $"attribute_exists(m.sub) returned {Py.Repr(nestedPresent)}, expected the two items carrying m.sub");

        var nestedAbsent = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND attribute_not_exists(m.nope)");
        Ensure(
            nestedAbsent.SequenceEqual([("p1", "a1"), ("p1", "a2"), ("p1", "b1")]),
            $"attribute_not_exists(m.nope) returned {Py.Repr(nestedAbsent)}, expected every item (m.nope never exists)");

        var orOfFunctions = await Select(ddb, $"SELECT * FROM \"{t}\" WHERE pk='p1' AND (attribute_exists(\"ss\") OR attribute_exists(m))");
        Ensure(
            orOfFunctions.SequenceEqual([("p1", "a1"), ("p1", "a2")]),
            $"(attribute_exists(ss) OR attribute_exists(m)) returned {Py.Repr(orOfFunctions)}, expected the two items carrying ss or m");
    }
}
