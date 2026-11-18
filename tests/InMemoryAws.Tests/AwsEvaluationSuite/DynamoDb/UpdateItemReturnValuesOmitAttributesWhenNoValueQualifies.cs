// Translated from behavioral/dynamodb/check_update_item_return_values_omit_attributes_when_no_value_qualifies.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-return-values-omit-attributes-when-no-value-qualifies</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemReturnValuesOmitAttributesWhenNoValueQualifies
{
    /// <summary>
    /// UPDATED_NEW after REMOVE, UPDATED_OLD after SET of a new attribute, and UPDATED_NEW after
    /// a legacy AttributeUpdates DELETE omit Attributes entirely, while each update still applies.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-return-values-omit-attributes-when-no-value-qualifies")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-rvomit-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("rv")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("rv")),
            ("gone", S("present")),
            ("m", M(("k", S("v")))),
            ("keep", S("old"))));

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE gone",
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            resp.Attributes is null,
            $"REMOVE of a present attribute with UPDATED_NEW returned {Py.Repr(resp.Attributes)}");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET fresh = :f",
            ExpressionAttributeValues = Item((":f", S("new"))),
            ReturnValues = ReturnValue.UPDATED_OLD,
        });
        Ensure(
            resp.Attributes is null,
            $"SET of a brand-new attribute with UPDATED_OLD returned {Py.Repr(resp.Attributes)}");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>(StringComparer.Ordinal)
            {
                ["m"] = new AttributeValueUpdate { Action = AttributeAction.DELETE },
            },
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            resp.Attributes is null,
            $"AttributeUpdates DELETE of a map with UPDATED_NEW returned {Py.Repr(resp.Attributes)}");

        // Positive control: a value that does qualify is returned.
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET keep = :k",
            ExpressionAttributeValues = Item((":k", S("new"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("keep", S("new")))),
            $"SET keep with UPDATED_NEW returned {Py.Repr(resp.Attributes)}, expected keep=new");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var want = Item(("pk", S("rv")), ("fresh", S("new")), ("keep", S("new")));
        Ensure(Py.Equal(item, want), $"after the updates the item is {Py.Repr(item)}, expected {Py.Repr(want)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (AmazonDynamoDBException ex)
            {
                if (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
                {
                    return;
                }

                if (ex.ErrorCode != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(300))
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
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }
}
