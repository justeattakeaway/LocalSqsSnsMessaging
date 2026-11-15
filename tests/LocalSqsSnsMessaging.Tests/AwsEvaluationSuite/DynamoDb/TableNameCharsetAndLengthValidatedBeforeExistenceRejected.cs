// Translated from behavioral/dynamodb/check_table_name_charset_and_length_validated_before_existence_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-name-charset-and-length-validated-before-existence-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableNameCharsetAndLengthValidatedBeforeExistenceRejected
{
    private const string CheckId = "table-name-charset-and-length-validated-before-existence-rejected";
    private const string ErrValidation = "ValidationException";
    private const string ErrNotFound = "ResourceNotFoundException";

    /// <summary>The AWS error code and message <paramref name="fn"/> raises; an unexpected success fails loudly.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// A TableName outside length 3..255 over [a-zA-Z0-9_.-] is ValidationException on GetItem, PutItem,
    /// Query and DeleteItem before any lookup, while legal absent names (including both length bounds)
    /// are ResourceNotFoundException, and no refused write creates a table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.table-name-charset-and-length-validated-before-existence-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var real = FidelityContext.UniqueName("sim2real-behav-nm-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = real,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(real));
        await ddb.WaitForTableExists(real, delaySeconds: 2, maxAttempts: 250);

        // Positive control: all four operations work against a well-formed name that DOES exist, so a
        // target that refuses every request cannot pass the rejections below.
        await ddb.PutItemAsync(real, Item(("pk", S("ctl"))));
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = real, Key = Item(("pk", S("ctl"))), ConsistentRead = true });
        Ensure(
            Py.Equal(got.Item, Item(("pk", S("ctl")))),
            $"positive control: PutItem/GetItem on a legal, existing name must round-trip, got {Py.Repr(got.Item)}");
        var q = await ddb.QueryAsync(new QueryRequest
        {
            TableName = real,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("ctl"))),
            ConsistentRead = true,
        });
        Ensure(q.Count == 1, $"positive control: Query must find the control item, got {Py.Repr(q.Items)}");
        await ddb.DeleteItemAsync(real, Item(("pk", S("ctl"))));
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = real, Key = Item(("pk", S("ctl"))), ConsistentRead = true });
        Ensure(
            gone.Item is null,
            $"positive control: DeleteItem on a legal, existing name must remove the item, got {Py.Repr(gone.Item)}");

        List<(string Op, Func<Task> Call)> Ops(string name) =>
        [
            ("GetItem", () => ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Item(("pk", S("x"))) })),
            ("PutItem", () => ddb.PutItemAsync(new PutItemRequest { TableName = name, Item = Item(("pk", S("x"))) })),
            ("Query", () => ddb.QueryAsync(new QueryRequest
            {
                TableName = name,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = Item((":p", S("x"))),
            })),
            ("DeleteItem", () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = name, Key = Item(("pk", S("x"))) })),
        ];

        // --- names the grammar forbids: rejected before the table is ever looked up ---
        var illegal = new List<(string Why, string Name)>
        {
            ("two characters (below the 3-character floor)", "ab"),
            ("a '/' outside [a-zA-Z0-9_.-]", "a/b"),
            ("a space outside [a-zA-Z0-9_.-]", "a b"),
            ("a ':' outside [a-zA-Z0-9_.-]", "a:b"),
            ("256 characters (above the 255-character ceiling)", new string('a', 256)),
        };
        const string pattern = "Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+";
        var constraint = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["two characters (below the 3-character floor)"] = "Member must have length greater than or equal to 3",
            ["a '/' outside [a-zA-Z0-9_.-]"] = pattern,
            ["a space outside [a-zA-Z0-9_.-]"] = pattern,
            ["a ':' outside [a-zA-Z0-9_.-]"] = pattern,
            ["256 characters (above the 255-character ceiling)"] = "Member must have length less than or equal to 255",
        };
        foreach (var (why, name) in illegal)
        {
            var expectedMessage = $"1 validation error detected: Value '{name}' at 'tableName' failed to satisfy constraint: {constraint[why]}";
            foreach (var (op, call) in Ops(name))
            {
                var (code, message) = await ErrorCode(call, $"{op} with a name that is {why}");
                Ensure(
                    code == ErrValidation,
                    $"{op} with a TableName that is {why} must be refused {ErrValidation} before the table lookup, got {Py.Repr(code)}");
                Ensure(
                    message == expectedMessage,
                    $"{op} with a TableName that is {why}: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
            }
        }

        // --- names the grammar allows but nothing answers to: they reach the lookup ---
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var legalAbsent = new List<(string Why, string Name)>
        {
            ("the inclusive 3-character floor", "a" + suffix[..2]),
            ("the inclusive 255-character ceiling", ("sim2real-behav-ghost-" + suffix).PadRight(255, 'z')),
            ("a dotted/underscored name using every allowed class", $"sim2real.behav_ghost-{suffix}"),
        };
        foreach (var (why, name) in legalAbsent)
        {
            Ensure(name.Length is >= 3 and <= 255, $"test data error: {Py.Repr(name)} is not within the legal length");
            foreach (var (op, call) in Ops(name))
            {
                var (code, message2) = await ErrorCode(call, $"{op} with a legal absent name at {why}");
                Ensure(
                    code == ErrNotFound,
                    $"{op} with a TableName at {why} satisfies the grammar and must therefore reach existence checking and be refused {ErrNotFound}, got {Py.Repr(code)}");
                Ensure(
                    message2 == "Requested resource not found",
                    $"expected AWS's exact ResourceNotFoundException message, got {Py.Repr(message2)}");
            }
        }

        // --- effect absent: none of the refused PutItems brought a table into being ---
        var live = new HashSet<string>(StringComparer.Ordinal);
        string? start = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start });
            live.UnionWith(page.TableNames ?? []);
            start = page.LastEvaluatedTableName;
        }
        while (start is not null);

        foreach (var name in illegal.Select(i => i.Name).Concat(legalAbsent.Select(l => l.Name)))
        {
            Ensure(
                !live.Contains(name),
                $"a refused PutItem must not create the table it addressed, but {Py.Repr(name)} is present in ListTables");
        }

        foreach (var (why, name) in legalAbsent)
        {
            var (code, message2) = await ErrorCode(() => ddb.DescribeTableAsync(name), $"DescribeTable on the ghost at {why}");
            Ensure(
                code == ErrNotFound,
                $"the ghost table at {why} must still not exist after the refused writes, got {Py.Repr(code)}");
            Ensure(
                message2 == $"Requested resource not found: Table: {name} not found",
                $"expected AWS's exact ResourceNotFoundException message, got {Py.Repr(message2)}");
        }
    }
}
