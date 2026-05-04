namespace SkirClient;

/// <summary>
/// Error returned by <see cref="ServiceClient.InvokeRemote{TRequest,TResponse}(Method{TRequest,TResponse}, TRequest, IEnumerable{KeyValuePair{string, string}}?, CancellationToken)"/>
/// when the server responds with a non-2xx status code or when a network-level failure occurs.
/// </summary>
public sealed class RpcError : Exception
{
    public ushort StatusCode { get; }

    public RpcError(ushort statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Sends RPCs to a SkirRPC service.
/// </summary>
public sealed class ServiceClient
{
    private readonly string _serviceUrl;
    private readonly List<KeyValuePair<string, string>> _defaultHeaders = [];
    private readonly HttpClient _httpClient;

    public ServiceClient(string serviceUrl)
    {
        if (serviceUrl.Contains('?', StringComparison.Ordinal))
            throw new ArgumentException("service URL must not contain a query string", nameof(serviceUrl));

        _serviceUrl = serviceUrl;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Adds a default HTTP header sent with every invocation.
    /// </summary>
    public ServiceClient WithDefaultHeader(string key, string value)
    {
        _defaultHeaders.Add(new KeyValuePair<string, string>(key, value));
        return this;
    }

    /// <summary>
    /// Invokes <paramref name="method"/> on the remote service with the given request.
    /// </summary>
    public async Task<TResponse> InvokeRemote<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        TRequest request,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        string requestJson = method.RequestSerializer.ToJson(request);

        // Wire body: "MethodName:number::requestJson"
        string wireBody = $"{method.Name}:{method.Number}::{requestJson}";

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, _serviceUrl)
        {
            Content = new StringContent(wireBody)
        };
        requestMessage.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain")
            {
                CharSet = "utf-8"
            };

        foreach (var header in _defaultHeaders)
            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (extraHeaders != null)
        {
            foreach (var header in extraHeaders)
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            ushort statusCode = ex.StatusCode.HasValue ? (ushort)ex.StatusCode.Value : (ushort)0;
            throw new RpcError(statusCode, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            throw new RpcError(0, ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ushort statusCode = (ushort)response.StatusCode;
                string message = string.Empty;
                string? contentType = response.Content.Headers.ContentType?.ToString();
                if (contentType != null && contentType.Contains("text/plain", StringComparison.Ordinal))
                    message = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                throw new RpcError(statusCode, message);
            }

            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new RpcError(0, $"failed to read response body: {ex.Message}");
            }

            try
            {
                return method.ResponseSerializer.FromJson(responseBody, keepUnrecognizedValues: true);
            }
            catch (Exception ex)
            {
                throw new RpcError(0, $"failed to decode response: {ex.Message}");
            }
        }
    }
}
