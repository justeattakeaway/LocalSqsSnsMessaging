using LocalSqsSnsMessaging.DynamoDb.Model;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// Operations for features that need services or infrastructure the bus doesn't emulate: S3
/// exports and imports, Kinesis destinations and bulk operations. Each answers
/// <c>UnknownOperationException</c>.
/// </summary>
internal sealed partial class InternalDynamoDbClient
{
    public Task<CancelBulkOperationResponse> CancelBulkOperationAsync(CancelBulkOperationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CancelBulkOperation is not supported by the in-memory DynamoDB.");

    public Task<DescribeBulkOperationResponse> DescribeBulkOperationAsync(DescribeBulkOperationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DescribeBulkOperation is not supported by the in-memory DynamoDB.");

    public Task<KinesisStreamingDestinationOutput> DisableKinesisStreamingDestinationAsync(KinesisStreamingDestinationInput request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DisableKinesisStreamingDestination is not supported by the in-memory DynamoDB.");

    public Task<KinesisStreamingDestinationOutput> EnableKinesisStreamingDestinationAsync(KinesisStreamingDestinationInput request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("EnableKinesisStreamingDestination is not supported by the in-memory DynamoDB.");

    public Task<ExportTableToPointInTimeResponse> ExportTableToPointInTimeAsync(ExportTableToPointInTimeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ExportTableToPointInTime is not supported by the in-memory DynamoDB.");

    public Task<ImportTableResponse> ImportTableAsync(ImportTableRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ImportTable is not supported by the in-memory DynamoDB.");

    public Task<ListBulkOperationsResponse> ListBulkOperationsAsync(ListBulkOperationsRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListBulkOperations is not supported by the in-memory DynamoDB.");

    public Task<StartBulkPutItemsResponse> StartBulkPutItemsAsync(StartBulkPutItemsRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("StartBulkPutItems is not supported by the in-memory DynamoDB.");

    public Task<UpdateKinesisStreamingDestinationResponse> UpdateKinesisStreamingDestinationAsync(UpdateKinesisStreamingDestinationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("UpdateKinesisStreamingDestination is not supported by the in-memory DynamoDB.");

}
