using System.Net;
using System.Text.Json;

#pragma warning disable CA1032 // Implement standard exception constructors - internal types
#pragma warning disable CA1064 // Exceptions should be public - internal implementation exceptions

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>An error DynamoDB answers with: its wire code (the <c>__type</c>) and message.</summary>
internal class DynamoDbException : AwsServiceException
{
    public DynamoDbException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>A condition that did not hold: <c>ConditionalCheckFailedException</c>, with the item when asked for.</summary>
internal sealed class DynamoDbConditionalCheckFailedException : DynamoDbException, IAwsJsonErrorMembers
{
    public const string DefaultMessage = "The conditional request failed";

    public DynamoDbConditionalCheckFailedException(Dictionary<string, DynamoValue>? item)
        : base("ConditionalCheckFailedException", DefaultMessage)
    {
        Item = item;
    }

    /// <summary>The item as it was, when the request asked for it with <c>ReturnValuesOnConditionCheckFailure</c>.</summary>
    public Dictionary<string, DynamoValue>? Item { get; }

    public void WriteMembers(Utf8JsonWriter writer)
    {
        if (Item is not null)
        {
            writer.WritePropertyName(nameof(Item));
            DynamoValueJson.WriteItem(writer, Item);
        }
    }
}

/// <summary>Why one item of a transaction was cancelled: <c>None</c> for an item that was fine.</summary>
internal sealed record CancellationReason(string? Code, string? Message = null, Dictionary<string, DynamoValue>? Item = null)
{
    public static CancellationReason None { get; } = new("None");
}

/// <summary>A cancelled transaction: <c>TransactionCanceledException</c>, with a reason per item, in request order.</summary>
internal sealed class DynamoDbTransactionCanceledException : DynamoDbException, IAwsJsonErrorMembers
{
    public DynamoDbTransactionCanceledException(IReadOnlyList<CancellationReason> reasons)
        : base("TransactionCanceledException", MessageFor(reasons))
    {
        Reasons = reasons;
    }

    public IReadOnlyList<CancellationReason> Reasons { get; }

    private static string MessageFor(IReadOnlyList<CancellationReason> reasons) =>
        "Transaction cancelled, please refer cancellation reasons for specific reasons ["
        + string.Join(", ", reasons.Select(r => r.Code ?? "None")) + "]";

    public void WriteMembers(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("CancellationReasons");
        foreach (var reason in Reasons)
        {
            writer.WriteStartObject();
            if (reason.Item is not null)
            {
                writer.WritePropertyName("Item");
                DynamoValueJson.WriteItem(writer, reason.Item);
            }

            writer.WriteString("Code", reason.Code ?? "None");
            if (reason.Message is not null)
            {
                writer.WriteString("Message", reason.Message);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

/// <summary>The errors DynamoDB answers with, worded as it words them.</summary>
internal static class DynamoDbErrors
{
    public const string RequestedResourceNotFound = "Requested resource not found";

    public static DynamoDbException Validation(string message) => new("ValidationException", message);

    /// <summary>A refusal stated inside the validation envelope: <c>1 validation error detected: ...</c>.</summary>
    public static DynamoDbException ValidationClause(string clause) => new("ValidationException", AwsValidation.Envelope(clause));

    /// <summary><c>One or more parameter values were invalid: ...</c>.</summary>
    public static DynamoDbException InvalidParameter(string detail) =>
        new("ValidationException", "One or more parameter values were invalid: " + detail);

    public static DynamoDbException ResourceNotFound(string message = RequestedResourceNotFound) =>
        new("ResourceNotFoundException", message);

    /// <summary>A table a data-plane request names that does not exist.</summary>
    public static DynamoDbException TableNotFound() => ResourceNotFound();

    /// <summary>A table a control-plane request names that does not exist.</summary>
    public static DynamoDbException TableNotFound(string name) =>
        ResourceNotFound($"Requested resource not found: Table: {name} not found");

    /// <summary>A request a resource policy refuses: <c>AccessDeniedException</c>, with DynamoDB's usual HTTP 400.</summary>
    public static DynamoDbException AccessDenied(string message) => new("AccessDeniedException", message);

    public static DynamoDbException ResourceInUse(string message) => new("ResourceInUseException", message);

    public static DynamoDbException LimitExceeded(string message) => new("LimitExceededException", message);

    public static DynamoDbException InternalServerError(string message) =>
        new("InternalServerError", message) { StatusCode = HttpStatusCode.InternalServerError };

    public static DynamoDbException KeySchemaMismatch() =>
        Validation("The provided key element does not match the schema");
}
