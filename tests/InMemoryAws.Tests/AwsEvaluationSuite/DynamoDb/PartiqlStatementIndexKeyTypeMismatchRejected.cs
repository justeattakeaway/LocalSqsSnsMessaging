// Translated from behavioral/dynamodb/check_partiql_statement_index_key_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-statement-index-key-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlStatementIndexKeyTypeMismatchRejected
{
    private const string CheckId = "partiql-statement-index-key-type-mismatch-rejected";
    private const string Validation = "ValidationException";

    private const string EmptyIndexKey =
        "One or more parameter values are not valid. The update expression attempted to update a secondary "
        + "index key to a value that is not supported. The AttributeValue for a key attribute cannot contain "
        + "an empty string value.";

    private static string Mismatch(string key, string expected, string actual, string index) =>
        $"One or more parameter values were invalid: Type mismatch for Index Key {key} Expected: {expected} Actual: {actual} IndexName: {index}";

    /// <summary>The error <paramref name="fn"/> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<AwsError> Refusal<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// ExecuteStatement refuses an INSERT or UPDATE that gives a GSI or LSI key attribute the wrong type
    /// or an empty string, writes nothing, and the well-typed statements are served through both indexes.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-statement-index-key-type-mismatch-rejected")]
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
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N), new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("gsk", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "by-l",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "by-g",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH), new KeySchemaElement("gsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(t);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 300);

        var seed = Item(("pk", S("p")), ("sk", S("seed")), ("note", S("kept")));
        await ddb.PutItemAsync(t, seed);

        (string Statement, string Message)[] refused =
        [
            ($"INSERT INTO \"{t}\" VALUE {{'pk': 'p', 'sk': 'bad', 'gpk': 'g', 'gsk': 'x'}}", Mismatch("gsk", "N", "S", "by-g")),
            ($"UPDATE \"{t}\" SET lsk = 'x' WHERE pk = 'p' AND sk = 'seed'", Mismatch("lsk", "N", "S", "by-l")),
            ($"UPDATE \"{t}\" SET gpk = 5 WHERE pk = 'p' AND sk = 'seed'", Mismatch("gpk", "S", "N", "by-g")),
            ($"UPDATE \"{t}\" SET gpk = '' WHERE pk = 'p' AND sk = 'seed'", EmptyIndexKey),
        ];
        foreach (var (statement, message) in refused)
        {
            var err = await Refusal(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement }));
            Ensure(err.Code == Validation, $"{statement}: expected {Validation}, got {err}");
            Ensure(err.Message == message, $"{statement}: expected {Py.Repr(message)}, got {Py.Repr(err.Message)}");
        }

        var bad = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("p")), ("sk", S("bad"))), ConsistentRead = true })).Item;
        Ensure(bad is not { Count: > 0 }, $"a refused INSERT wrote {Py.Repr(bad)}");
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("p")), ("sk", S("seed"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(kept, seed), $"a refused UPDATE changed the seeded item: {Py.Repr(kept)}");

        // Positive control: the same statements with well-typed index keys apply and are indexed.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk': 'p', 'sk': 'good', 'gpk': 'g', 'gsk': 5}}" });
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{t}\" SET lsk = 3 WHERE pk = 'p' AND sk = 'seed'" });

        var local = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "by-l",
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
        })).Items;
        Ensure(
            Py.Equal(local, new[] { Item(("pk", S("p")), ("sk", S("seed")), ("note", S("kept")), ("lsk", N("3"))) }),
            $"the LSI held {Py.Repr(local)}");

        var clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> glob;
        while (true)
        {
            // A GSI read is eventually consistent, so poll until the write is indexed.
            glob = (await ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                IndexName = "by-g",
                KeyConditionExpression = "gpk = :g",
                ExpressionAttributeValues = Item((":g", S("g"))),
            })).Items;
            if (glob.Count > 0 || clock.Elapsed.TotalSeconds > 60)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            Py.Equal(glob, new[] { Item(("pk", S("p")), ("sk", S("good")), ("gpk", S("g")), ("gsk", N("5"))) }),
            $"the GSI held {Py.Repr(glob)}");
    }
}
