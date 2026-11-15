// Translated from behavioral/dynamodb/check_transact_write_items_invalid_table_name_names_member_path.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-invalid-table-name-names-member-path</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsInvalidTableNameNamesMemberPath
{
    private const string Check = "transact-write-items-invalid-table-name-names-member-path";
    private const string ErrValidation = "ValidationException";
    private const string BadName = "bad name";

    private static readonly Dictionary<string, string> ActionPath = new(StringComparer.Ordinal)
    {
        ["Put"] = "put",
        ["Update"] = "update",
        ["Delete"] = "delete",
        ["ConditionCheck"] = "conditionCheck",
    };

    private static readonly string[] Kinds = ["Put", "Update", "Delete", "ConditionCheck"];
    private static readonly int[] Positions = [1, 2];
    private static readonly string[] ControlKeys = ["control-a", "control-b"];

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where ValidationException was required: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>A single TransactItems entry of <paramref name="kind"/> addressing <paramref name="tableName"/>.</summary>
    private static TransactWriteItem BadAction(string kind, string tableName) => kind switch
    {
        "Put" => new TransactWriteItem { Put = new Put { TableName = tableName, Item = Item(("pk", S("victim"))) } },
        "Update" => new TransactWriteItem
        {
            Update = new Update
            {
                TableName = tableName,
                Key = Item(("pk", S("victim"))),
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", N("1"))),
            },
        },
        "Delete" => new TransactWriteItem { Delete = new Delete { TableName = tableName, Key = Item(("pk", S("victim"))) } },
        _ => new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = tableName,
                Key = Item(("pk", S("victim"))),
                ConditionExpression = "attribute_not_exists(pk)",
            },
        },
    };

    /// <summary>A valid Put used to fill the position the bad action does not occupy.</summary>
    private static TransactWriteItem Sibling(string table) =>
        new() { Put = new Put { TableName = table, Item = Item(("pk", S("sibling"))) } };

    /// <summary>
    /// A TransactWriteItems action whose TableName violates the name pattern is a top-level
    /// ValidationException naming the member path by position and kind; the whole transaction,
    /// including its valid sibling action, writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-invalid-table-name-names-member-path")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Positive control: a normal two-item transaction commits, so every rejection below is
        // attributable to the bad TableName rather than to the harness being unable to transact at all.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("control-a"))) } },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("control-b"))) } },
            ],
        });
        foreach (var key in ControlKeys)
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;
            Ensure(item is not null, $"positive control: {Py.Repr(key)} should have been written by a valid transaction");
        }

        foreach (var kind in Kinds)
        {
            foreach (var position in Positions)
            {
                var bad = BadAction(kind, BadName);
                var sibling = Sibling(table);
                List<TransactWriteItem> items = position == 1 ? [bad, sibling] : [sibling, bad];
                var (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }));
                Ensure(
                    code == ErrValidation,
                    $"{kind} with an invalid TableName at position {position} answered {Py.Repr(code)} ({Py.Repr(message)}), expected {ErrValidation}");
                var path = $"transactItems.{position}.member.{ActionPath[kind]}.tableName";
                var expected = $"1 validation error detected: Value '{BadName}' at '{path}' failed to satisfy "
                    + "constraint: Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+";
                Ensure(message == expected, $"{kind} at position {position}: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
            }
        }

        // Effect absent: none of the 8 rejected transactions wrote their valid sibling action.
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("sibling"))), ConsistentRead = true })).Item;
        Ensure(got is null, $"a transaction rejected for an invalid TableName must not write its sibling action, got {Py.Repr(got)}");
    }
}
