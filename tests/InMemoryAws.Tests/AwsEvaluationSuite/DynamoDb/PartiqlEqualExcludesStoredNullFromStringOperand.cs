// Translated from behavioral/dynamodb/check_partiql_equal_excludes_stored_null_from_string_operand.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-equal-excludes-stored-null-from-string-operand</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlEqualExcludesStoredNullFromStringOperand
{
    private const string CheckId = "partiql-equal-excludes-stored-null-from-string-operand";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>Assert <paramref name="fn"/> is refused with the exact (code, message) pair.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task<HashSet<string>> SelectPks(IAmazonDynamoDB ddb, string table, string where, List<AttributeValue> parameters)
    {
        var items = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE {where}",
            Parameters = parameters,
            ConsistentRead = true,
        })).Items;
        return [.. items.Select(i => i["pk"].S)];
    }

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    /// <summary>
    /// A PartiQL UPDATE WHERE condition comparing a stored NULL-typed attribute to a String
    /// operand with = is refused as a failed condition, leaving the item unchanged, because = does
    /// not treat a stored NULL as equal to a non-NULL value; a matching String condition applies.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-operators.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-equal-excludes-stored-null-from-string-operand")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlnulleq-");
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

        var rows = new Dictionary<string, AttributeValue>
        {
            ["nullval"] = Null(),
            ["strval"] = S("x"),
        };
        foreach (var (pk, val) in rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("val", val)));
        }

        var allPks = rows.Keys.ToHashSet();

        // A String operand matches only the stored String, never the stored NULL.
        var matched = await SelectPks(ddb, table, "val = ?", [S("x")]);
        Ensure(matched.SetEquals(["strval"]), $"String = matched {Py.Repr(Sorted(matched))}, expected ['strval']");
        var notMatched = await SelectPks(ddb, table, "val <> ?", [S("x")]);
        Ensure(notMatched.SetEquals(allPks.Except(["strval"])), $"String <> matched {Py.Repr(Sorted(notMatched))}, expected everything but strval");

        // A NULL operand matches only the stored NULL, never the stored String.
        matched = await SelectPks(ddb, table, "val = ?", [Null()]);
        Ensure(matched.SetEquals(["nullval"]), $"NULL = matched {Py.Repr(Sorted(matched))}, expected ['nullval']");
        notMatched = await SelectPks(ddb, table, "val <> ?", [Null()]);
        Ensure(notMatched.SetEquals(allPks.Except(["nullval"])), $"NULL <> matched {Py.Repr(Sorted(notMatched))}, expected everything but nullval");

        // The same comparison agrees as a PartiQL UPDATE write condition: a NULL item does not
        // satisfy `val = <String>`, so the write is refused and the item stays as it was.
        var before = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("nullval"))), ConsistentRead = true })).Item;
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{table}\" SET marker = ? WHERE pk = ? AND val = ?",
                Parameters = [S("should-not-apply"), S("nullval"), S("x")],
            }),
            Ccf);
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("nullval"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, before), $"a String write condition against a stored NULL must leave the item untouched, got {Py.Repr(after)}");

        // Positive control: the matching String condition applies to the String item.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET marker = ? WHERE pk = ? AND val = ?",
            Parameters = [S("applied"), S("strval"), S("x")],
        });
        after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("strval"))), ConsistentRead = true })).Item;
        Ensure(
            after.TryGetValue("marker", out var marker) && Py.Equal(marker, S("applied")),
            $"the matching String UPDATE WHERE condition must have applied, got {Py.Repr(after.GetValueOrDefault("marker"))}");
    }
}
