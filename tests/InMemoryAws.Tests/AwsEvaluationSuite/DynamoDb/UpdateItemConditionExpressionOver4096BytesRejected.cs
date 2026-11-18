// Translated from behavioral/dynamodb/check_update_item_condition_expression_over_4096_bytes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-condition-expression-over-4096-bytes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemConditionExpressionOver4096BytesRejected
{
    /// <summary>
    /// UpdateItem refuses a ConditionExpression over 4096 bytes as ValidationException and
    /// leaves the item unchanged; a well-formed ConditionExpression in the same shape still applies.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-condition-expression-over-4096-bytes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-upd-cond-size-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        await ddb.PutItemAsync(table, Item(("pk", S("k1")), ("val", S("orig"))));

        // Positive control: a well-formed ConditionExpression still applies the update.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("k1"))),
            UpdateExpression = "SET val = :v",
            ConditionExpression = "attribute_exists(pk)",
            ExpressionAttributeValues = Item((":v", S("updated-control"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got, Item(("pk", S("k1")), ("val", S("updated-control")))),
            $"positive control: UpdateItem with a well-formed ConditionExpression read back {Py.Repr(got)}");

        var over4Kb = "attribute_exists(pk)" + new string(' ', 4097 - "attribute_exists(pk)".Length);
        Ensure(over4Kb.Length == 4097, $"ConditionExpression length {over4Kb.Length}, expected 4097");

        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("k1"))),
            UpdateExpression = "SET val = :v",
            ConditionExpression = over4Kb,
            ExpressionAttributeValues = Item((":v", S("should-not-apply"))),
        }));
        Ensure(
            (code, msg) == ("ValidationException",
                "1 validation error detected: Invalid ConditionExpression: Expression size has exceeded " +
                "the maximum allowed size;"),
            $"UpdateItem with a 4097-byte ConditionExpression gave {Py.Repr(code)}: {Py.Repr(msg)}");

        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got, Item(("pk", S("k1")), ("val", S("updated-control")))),
            $"a rejected ConditionExpression must not change the item, got {Py.Repr(got)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (Amazon.Runtime.AmazonServiceException exc)
            {
                if (exc.ErrorCode == notFound)
                {
                    return;
                }

                if (exc.ErrorCode != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var budget = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < budget)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}")
            : (error.Code, error.Message);
    }
}
