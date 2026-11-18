// Translated from behavioral/dynamodb/check_transact_write_items_missing_table_is_resource_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-missing-table-is-resource-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsMissingTableIsResourceNotFound
{
    private static readonly (string Code, string Message) Rnf = ("ResourceNotFoundException", "Requested resource not found");

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var limit = Math.Max(0.0, (timeLeft == 0 ? 300.0 : timeLeft) - 30.0);
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(limit))
        {
            status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(status)}");
    }

    /// <summary>Call fn(); assert the error carries exactly (code, message); an accepted call fails.</summary>
    private static async Task Error<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        Ensure(err.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(err.Code)}: {Py.Repr(new Dictionary<string, object?> { ["Message"] = err.Message, ["Code"] = err.Code })}");
        Ensure(err.Message == message, $"expected {Py.Repr(message)}, got {Py.Repr(err.Message)}");
    }

    /// <summary>
    /// A TransactWriteItems naming a table that does not exist, with no other defect, fails the
    /// whole call with ResourceNotFoundException and does not apply a sibling Put on a real table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-missing-table-is-resource-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-twirnf-");
        var missingTable = FidelityContext.UniqueName("sim2real-behav-ddb-twirnf-missing-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        // Positive control: a well-formed transaction against the real table is accepted and lands.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("control"))) } }],
        });
        var controlGot = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(controlGot, Item(("pk", S("control")))),
            $"positive control: expected the well-formed TransactWriteItems to land, got {Py.Repr(controlGot)}");

        // Break exactly one thing: a sibling Put addresses a table that was never created.
        await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = missingTable, Item = Item(("pk", S("x"))) } },
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("sibling-should-not-write"))) } },
                ],
            }),
            Rnf.Code,
            Rnf.Message);

        var siblingGot = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("sibling-should-not-write"))),
            ConsistentRead = true,
        })).Item;
        Ensure(
            siblingGot is null,
            $"a TransactWriteItems that failed with ResourceNotFoundException must not apply its sibling Put; got {Py.Repr(siblingGot)}");
    }
}
