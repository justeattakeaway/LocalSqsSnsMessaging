using System.ComponentModel;
using System.Net;
using Amazon.Runtime;

namespace LocalSqsSnsMessaging;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class ResponseHelper
{
    extension<T>(T response) where T : AmazonWebServiceResponse
    {
        public T SetCommonProperties()
        {
            response.HttpStatusCode = HttpStatusCode.OK;
            response.ResponseMetadata = new ResponseMetadata
            {
                RequestId = Guid.NewGuid().ToString()
            };

            response.ContentLength = 0L;

            return response;
        }
    }
}
