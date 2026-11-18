// Translated from behavioral/dynamodb/check_set_arithmetic_grouping_size_utf16_and_add_empty_string_to_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.set-arithmetic-grouping-size-utf16-and-add-empty-string-to-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetArithmeticGroupingSizeUtf16AndAddEmptyStringToSet
{
    private const string CheckId = "set-arithmetic-grouping-size-utf16-and-add-empty-string-to-set";
    private const string AstralChar = "\U0001F600";

    /// <summary>Wait for the table to become ACTIVE, bounded by the check's own remaining budget.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, FidelityContext ctx, string name)
    {
        var budget = Math.Max(10.0, Math.Min(120.0, ctx.TimeLeftSeconds - 10.0));
        const int delay = 2;
        var attempts = Math.Max(1, (int)Math.Floor(budget / delay));
        await ddb.WaitForTableExists(name, delaySeconds: delay, maxAttempts: attempts);
    }

    /// <summary>
    /// SET n = (n - :d) evaluates the parenthesised subtraction; a Query FilterExpression's
    /// size() counts a string in UTF-16 code units, so one astral-plane character counts as 2;
    /// ADD accepts an empty string as a new member of a string set.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.set-arithmetic-grouping-size-utf16-and-add-empty-string-to-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await WaitTableActive(ddb, ctx, t);

        // SET n = (n - :d): the parenthesised arithmetic is applied before assignment.
        await ddb.PutItemAsync(t, Item(("pk", S("p")), ("sk", S("counter")), ("n", N("10"))));
        var updated = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("p")), ("sk", S("counter"))),
            UpdateExpression = "SET n = (n - :d)",
            ExpressionAttributeValues = Item((":d", N("3"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(updated["n"], N("7")),
            $"SET n = (n - :d) from n=10 with :d=3 produced n={Py.Repr(updated.GetValueOrDefault("n"))}, expected {{'N': '7'}}");

        // size() on a Query FilterExpression counts UTF-16 code units: one astral-plane character
        // (one Unicode code point, encoded as a surrogate pair) counts as 2, the same as two ASCII
        // characters.
        (string Sk, string Text)[] rows =
        [
            ("one-astral-char", AstralChar),
            ("two-ascii-chars", "ab"),
            ("three-ascii-chars", "abc"),
        ];
        foreach (var (sk, text) in rows)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p")), ("sk", S(sk)), ("txt", S(text))));
        }

        var matched = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "size(txt) = :n",
            ExpressionAttributeValues = Item((":p", S("p")), (":n", N("2"))),
            ConsistentRead = true,
        });
        var matchedSks = matched.Items.Select(item => item["sk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            matchedSks.SetEquals(["one-astral-char", "two-ascii-chars"]),
            $"size(txt) = 2 matched {{{string.Join(", ", matchedSks.Order(StringComparer.Ordinal).Select(Py.Repr))}}}, expected {{'one-astral-char', 'two-ascii-chars'}}: "
            + "a single astral-plane character must count as 2 UTF-16 code units, the same as two "
            + "ASCII characters, and 'abc' (3 code units) must not match");

        // ADD accepts an empty string as a new member of an existing string set.
        await ddb.PutItemAsync(t, Item(("pk", S("p")), ("sk", S("tags")), ("tags", SS("x"))));
        var added = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("p")), ("sk", S("tags"))),
            UpdateExpression = "ADD tags :e",
            ExpressionAttributeValues = Item((":e", SS(""))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            added["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["", "x"]),
            $"ADD tags :e with :e={{'SS': ['']}} against an existing SS of ['x'] produced {Py.Repr(added.GetValueOrDefault("tags"))}, expected the set {{'', 'x'}}");
    }
}
