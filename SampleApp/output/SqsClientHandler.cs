using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

sealed class SqsClientHandler : HttpClientHandler
{
    private readonly IAmazonSQS _innerClient;

    public SqsClientHandler(IAmazonSQS innerClient)
    {
        _innerClient = innerClient;
    }
    
    private static HttpResponseMessage CreateErrorResponse(AmazonServiceException ex)
    {
        var statusCode = ex switch
        {
            InvalidMessageContentsException => HttpStatusCode.BadRequest,
            BatchEntryIdsNotDistinctException => HttpStatusCode.BadRequest,
            BatchRequestTooLongException => HttpStatusCode.BadRequest,
            EmptyBatchRequestException => HttpStatusCode.BadRequest,
            InvalidAttributeNameException => HttpStatusCode.BadRequest,
            InvalidBatchEntryIdException => HttpStatusCode.BadRequest,
            InvalidSecurityException => HttpStatusCode.Forbidden,
            MessageNotInflightException => HttpStatusCode.BadRequest,
            OverLimitException => HttpStatusCode.TooManyRequests,
            PurgeQueueInProgressException => HttpStatusCode.Conflict,
            QueueDeletedRecentlyException => HttpStatusCode.Conflict,
            QueueDoesNotExistException => HttpStatusCode.NotFound,
            QueueNameExistsException => HttpStatusCode.Conflict,
            ReceiptHandleIsInvalidException => HttpStatusCode.BadRequest,
            TooManyEntriesInBatchRequestException => HttpStatusCode.BadRequest,
            UnsupportedOperationException => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError
        };
    
        return new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(new ErrorResponse(ex.Message, ex.ErrorCode, ex.RequestId), jsonTypeInfo: SqsJsonSerializerContext.Default.ErrorResponse)
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.TryGetValues("X-Amz-Target", out var values))
        {
            var targetHeaderValue = values.FirstOrDefault(x => x.StartsWith("AmazonSQS.", StringComparison.Ordinal));
            if (targetHeaderValue != null)
            {
                var target = targetHeaderValue["AmazonSQS.".Length..];

                switch (target)
                {
                    case "AddPermission":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<AddPermissionRequest>(SqsJsonSerializerContext.Default.AddPermissionRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.AddPermissionAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<AddPermissionResponse>(result, SqsJsonSerializerContext.Default.AddPermissionResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "CancelMessageMoveTask":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<CancelMessageMoveTaskRequest>(SqsJsonSerializerContext.Default.CancelMessageMoveTaskRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.CancelMessageMoveTaskAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<CancelMessageMoveTaskResponse>(result, SqsJsonSerializerContext.Default.CancelMessageMoveTaskResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "ChangeMessageVisibilityBatch":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<ChangeMessageVisibilityBatchRequest>(SqsJsonSerializerContext.Default.ChangeMessageVisibilityBatchRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.ChangeMessageVisibilityBatchAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<ChangeMessageVisibilityBatchResponse>(result, SqsJsonSerializerContext.Default.ChangeMessageVisibilityBatchResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "ChangeMessageVisibility":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<ChangeMessageVisibilityRequest>(SqsJsonSerializerContext.Default.ChangeMessageVisibilityRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.ChangeMessageVisibilityAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<ChangeMessageVisibilityResponse>(result, SqsJsonSerializerContext.Default.ChangeMessageVisibilityResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "CreateQueue":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<CreateQueueRequest>(SqsJsonSerializerContext.Default.CreateQueueRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.CreateQueueAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<CreateQueueResponse>(result, SqsJsonSerializerContext.Default.CreateQueueResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "DeleteMessageBatch":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<DeleteMessageBatchRequest>(SqsJsonSerializerContext.Default.DeleteMessageBatchRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.DeleteMessageBatchAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<DeleteMessageBatchResponse>(result, SqsJsonSerializerContext.Default.DeleteMessageBatchResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "DeleteMessage":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<DeleteMessageRequest>(SqsJsonSerializerContext.Default.DeleteMessageRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.DeleteMessageAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<DeleteMessageResponse>(result, SqsJsonSerializerContext.Default.DeleteMessageResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "DeleteQueue":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<DeleteQueueRequest>(SqsJsonSerializerContext.Default.DeleteQueueRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.DeleteQueueAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<DeleteQueueResponse>(result, SqsJsonSerializerContext.Default.DeleteQueueResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "GetQueueAttributes":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<GetQueueAttributesRequest>(SqsJsonSerializerContext.Default.GetQueueAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.GetQueueAttributesAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<GetQueueAttributesResponse>(result, SqsJsonSerializerContext.Default.GetQueueAttributesResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "GetQueueUrl":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<GetQueueUrlRequest>(SqsJsonSerializerContext.Default.GetQueueUrlRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.GetQueueUrlAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<GetQueueUrlResponse>(result, SqsJsonSerializerContext.Default.GetQueueUrlResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "ListDeadLetterSourceQueues":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<ListDeadLetterSourceQueuesRequest>(SqsJsonSerializerContext.Default.ListDeadLetterSourceQueuesRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.ListDeadLetterSourceQueuesAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<ListDeadLetterSourceQueuesResponse>(result, SqsJsonSerializerContext.Default.ListDeadLetterSourceQueuesResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "ListMessageMoveTasks":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<ListMessageMoveTasksRequest>(SqsJsonSerializerContext.Default.ListMessageMoveTasksRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.ListMessageMoveTasksAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<ListMessageMoveTasksResponse>(result, SqsJsonSerializerContext.Default.ListMessageMoveTasksResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "ListQueues":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<ListQueuesRequest>(SqsJsonSerializerContext.Default.ListQueuesRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.ListQueuesAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<ListQueuesResponse>(result, SqsJsonSerializerContext.Default.ListQueuesResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "ListQueueTags":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<ListQueueTagsRequest>(SqsJsonSerializerContext.Default.ListQueueTagsRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.ListQueueTagsAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<ListQueueTagsResponse>(result, SqsJsonSerializerContext.Default.ListQueueTagsResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "PurgeQueue":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<PurgeQueueRequest>(SqsJsonSerializerContext.Default.PurgeQueueRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.PurgeQueueAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<PurgeQueueResponse>(result, SqsJsonSerializerContext.Default.PurgeQueueResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "ReceiveMessage":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<ReceiveMessageRequest>(SqsJsonSerializerContext.Default.ReceiveMessageRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.ReceiveMessageAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<ReceiveMessageResponse>(result, SqsJsonSerializerContext.Default.ReceiveMessageResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "RemovePermission":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<RemovePermissionRequest>(SqsJsonSerializerContext.Default.RemovePermissionRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.RemovePermissionAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<RemovePermissionResponse>(result, SqsJsonSerializerContext.Default.RemovePermissionResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "SendMessageBatch":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<SendMessageBatchRequest>(SqsJsonSerializerContext.Default.SendMessageBatchRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.SendMessageBatchAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<SendMessageBatchResponse>(result, SqsJsonSerializerContext.Default.SendMessageBatchResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "SendMessage":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<SendMessageRequest>(SqsJsonSerializerContext.Default.SendMessageRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.SendMessageAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<SendMessageResponse>(result, SqsJsonSerializerContext.Default.SendMessageResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "SetQueueAttributes":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<SetQueueAttributesRequest>(SqsJsonSerializerContext.Default.SetQueueAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.SetQueueAttributesAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<SetQueueAttributesResponse>(result, SqsJsonSerializerContext.Default.SetQueueAttributesResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "StartMessageMoveTask":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<StartMessageMoveTaskRequest>(SqsJsonSerializerContext.Default.StartMessageMoveTaskRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.StartMessageMoveTaskAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<StartMessageMoveTaskResponse>(result, SqsJsonSerializerContext.Default.StartMessageMoveTaskResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "TagQueue":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<TagQueueRequest>(SqsJsonSerializerContext.Default.TagQueueRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.TagQueueAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<TagQueueResponse>(result, SqsJsonSerializerContext.Default.TagQueueResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                    case "UntagQueue":
                    {
                        try
                        {
                            var requestObject = await request.Content!.ReadFromJsonAsync<UntagQueueRequest>(SqsJsonSerializerContext.Default.UntagQueueRequest, cancellationToken).ConfigureAwait(false);
                            var result = await _innerClient.UntagQueueAsync(requestObject, cancellationToken).ConfigureAwait(false);
    
                            var response = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new PooledJsonContent<UntagQueueResponse>(result, SqsJsonSerializerContext.Default.UntagQueueResponse)
                            };
                            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            return response;
                        }
                        catch (AmazonServiceException ex)
                        {
                            return CreateErrorResponse(ex);
                        }
                    }

                }
            }
        }
        
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = JsonContent.Create(new ErrorResponse("Unknown SQS operation", "UnknownOperation", string.Empty), jsonTypeInfo: SqsJsonSerializerContext.Default.ErrorResponse)
        };
    }
}

internal sealed record ErrorResponse(string Message, string ErrorCode, string RequestId);
