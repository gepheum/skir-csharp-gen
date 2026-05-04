using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SkirClient;

public sealed class RawResponse
{
    public required string Data { get; init; }
    public required ushort StatusCode { get; init; }
    public required string ContentType { get; init; }

    internal static RawResponse OkJson(string data) => new()
    {
        Data = data,
        StatusCode = 200,
        ContentType = "application/json"
    };

    internal static RawResponse OkHtml(string data) => new()
    {
        Data = data,
        StatusCode = 200,
        ContentType = "text/html; charset=utf-8"
    };

    internal static RawResponse BadRequest(string msg) => new()
    {
        Data = msg,
        StatusCode = 400,
        ContentType = "text/plain; charset=utf-8"
    };

    internal static RawResponse ServerError(string msg, ushort statusCode) => new()
    {
        Data = msg,
        StatusCode = statusCode,
        ContentType = "text/plain; charset=utf-8"
    };
}

public enum HttpErrorCode : ushort
{
    _400_BadRequest = 400,
    _401_Unauthorized = 401,
    _402_PaymentRequired = 402,
    _403_Forbidden = 403,
    _404_NotFound = 404,
    _405_MethodNotAllowed = 405,
    _406_NotAcceptable = 406,
    _407_ProxyAuthenticationRequired = 407,
    _408_RequestTimeout = 408,
    _409_Conflict = 409,
    _410_Gone = 410,
    _411_LengthRequired = 411,
    _412_PreconditionFailed = 412,
    _413_ContentTooLarge = 413,
    _414_UriTooLong = 414,
    _415_UnsupportedMediaType = 415,
    _416_RangeNotSatisfiable = 416,
    _417_ExpectationFailed = 417,
    _418_ImATeapot = 418,
    _421_MisdirectedRequest = 421,
    _422_UnprocessableContent = 422,
    _423_Locked = 423,
    _424_FailedDependency = 424,
    _425_TooEarly = 425,
    _426_UpgradeRequired = 426,
    _428_PreconditionRequired = 428,
    _429_TooManyRequests = 429,
    _431_RequestHeaderFieldsTooLarge = 431,
    _451_UnavailableForLegalReasons = 451,
    _500_InternalServerError = 500,
    _501_NotImplemented = 501,
    _502_BadGateway = 502,
    _503_ServiceUnavailable = 503,
    _504_GatewayTimeout = 504,
    _505_HttpVersionNotSupported = 505,
    _506_VariantAlsoNegotiates = 506,
    _507_InsufficientStorage = 507,
    _508_LoopDetected = 508,
    _510_NotExtended = 510,
    _511_NetworkAuthenticationRequired = 511,
}

public sealed class ServiceError : Exception
{
    public HttpErrorCode StatusCode { get; }

    public ServiceError(HttpErrorCode statusCode, string message, Exception? source = null)
        : base(message, source)
    {
        StatusCode = statusCode;
    }
}

public sealed class MethodErrorInfo<TMeta>
{
    public required Exception Error { get; init; }
    public required string MethodName { get; init; }
    public required string RawRequest { get; init; }
    public required TMeta RequestMeta { get; init; }
}

public sealed class Service<TMeta>
{
    private readonly bool _keepUnrecognizedValues;
    private readonly Func<MethodErrorInfo<TMeta>, bool> _canSendUnknownErrorMessage;
    private readonly Action<MethodErrorInfo<TMeta>> _errorLogger;
    private readonly string _studioAppJsUrl;
    private readonly Dictionary<long, MethodEntry<TMeta>> _byNum;
    private readonly Dictionary<string, long> _byName;

    internal Service(
        bool keepUnrecognizedValues,
        Func<MethodErrorInfo<TMeta>, bool> canSendUnknownErrorMessage,
        Action<MethodErrorInfo<TMeta>> errorLogger,
        string studioAppJsUrl,
        Dictionary<long, MethodEntry<TMeta>> byNum,
        Dictionary<string, long> byName)
    {
        _keepUnrecognizedValues = keepUnrecognizedValues;
        _canSendUnknownErrorMessage = canSendUnknownErrorMessage;
        _errorLogger = errorLogger;
        _studioAppJsUrl = studioAppJsUrl;
        _byNum = byNum;
        _byName = byName;
    }

    /// <summary>
    /// For GET requests in a standard HTTP stack, pass the decoded query string as body.
    /// For POST requests, pass the raw request body.
    /// </summary>
    public async Task<RawResponse> HandleRequest(string body, TMeta meta)
    {
        switch (body)
        {
            case "":
            case "studio":
                return ServeStudio(_studioAppJsUrl);
            case "list":
                return ServeList();
        }

        char first = body.Length == 0 ? ' ' : body[0];
        if (first == '{' || char.IsWhiteSpace(first))
            return await HandleJsonRequest(body, meta).ConfigureAwait(false);

        return await HandleColonRequest(body, meta).ConfigureAwait(false);
    }

    private RawResponse ServeList()
    {
        var entries = new List<MethodEntry<TMeta>>(_byNum.Values);
        entries.Sort((a, b) => a.Number.CompareTo(b.Number));

        var methods = new JsonArray();
        foreach (var entry in entries)
        {
            var obj = new JsonObject
            {
                ["method"] = entry.Name,
                ["number"] = entry.Number,
                ["request"] = ParseJsonOrNull(entry.RequestTypeDescriptorJson),
                ["response"] = ParseJsonOrNull(entry.ResponseTypeDescriptorJson),
            };
            if (!string.IsNullOrEmpty(entry.Doc))
                obj["doc"] = entry.Doc;
            methods.Add(obj);
        }

        var result = new JsonObject { ["methods"] = methods };
        string payload;
        try
        {
            payload = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            payload = "{}";
        }

        return RawResponse.OkJson(payload);
    }

    private async Task<RawResponse> HandleJsonRequest(string body, TMeta meta)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch
        {
            return RawResponse.BadRequest("bad request: invalid JSON");
        }

        if (root is not JsonObject obj)
            return RawResponse.BadRequest("bad request: expected JSON object");

        if (!obj.TryGetPropertyValue("method", out JsonNode? methodNode) || methodNode == null)
            return RawResponse.BadRequest("bad request: missing 'method' field in JSON");

        MethodEntry<TMeta>? entry;
        switch (methodNode)
        {
            case JsonValue value when value.TryGetValue<long>(out long number):
                if (!_byNum.TryGetValue(number, out entry))
                    return RawResponse.BadRequest($"bad request: method not found: {number}");
                break;

            case JsonValue value when value.TryGetValue<string>(out string? name):
                if (name == null || !_byName.TryGetValue(name, out long numberByName))
                    return RawResponse.BadRequest($"bad request: method not found: {name}");
                entry = _byNum[numberByName];
                break;

            default:
                return RawResponse.BadRequest("bad request: 'method' field must be a string or integer");
        }

        if (!obj.TryGetPropertyValue("request", out JsonNode? requestNode) || requestNode == null)
            return RawResponse.BadRequest("bad request: missing 'request' field in JSON");

        string requestJson = requestNode.ToJsonString();
        return await InvokeEntry(entry!, requestJson, _keepUnrecognizedValues, readable: true, meta).ConfigureAwait(false);
    }

    private async Task<RawResponse> HandleColonRequest(string body, TMeta meta)
    {
        // Format: "name:number:format:requestJson"
        string[] parts = body.Split(':', 4);
        if (parts.Length != 4)
            return RawResponse.BadRequest("bad request: invalid request format");

        string nameStr = parts[0];
        string numberStr = parts[1];
        string format = parts[2];
        string requestJson = string.IsNullOrEmpty(parts[3]) ? "{}" : parts[3];

        MethodEntry<TMeta>? entry;
        if (string.IsNullOrEmpty(numberStr))
        {
            if (!_byName.TryGetValue(nameStr, out long numberFromName))
                return RawResponse.BadRequest($"bad request: method not found: {nameStr}");
            entry = _byNum[numberFromName];
        }
        else
        {
            if (!long.TryParse(numberStr, out long number))
                return RawResponse.BadRequest("bad request: can't parse method number");
            if (!_byNum.TryGetValue(number, out entry))
                return RawResponse.BadRequest($"bad request: method not found: {nameStr}; number: {number}");
        }

        bool readable = format == "readable";
        return await InvokeEntry(entry!, requestJson, _keepUnrecognizedValues, readable, meta).ConfigureAwait(false);
    }

    private async Task<RawResponse> InvokeEntry(
        MethodEntry<TMeta> entry,
        string requestJson,
        bool keepUnrecognized,
        bool readable,
        TMeta meta)
    {
        string rawRequest = requestJson;
        try
        {
            string responseJson = await entry.Invoke(requestJson, keepUnrecognized, readable, meta).ConfigureAwait(false);
            return RawResponse.OkJson(responseJson);
        }
        catch (Exception ex)
        {
            var info = new MethodErrorInfo<TMeta>
            {
                Error = ex,
                MethodName = entry.Name,
                RawRequest = rawRequest,
                RequestMeta = meta,
            };

            _errorLogger(info);

            if (ex is ServiceError svc)
            {
                string msg = string.IsNullOrEmpty(svc.Message)
                    ? HttpStatusText((ushort)svc.StatusCode)
                    : svc.Message;
                return RawResponse.ServerError(msg, (ushort)svc.StatusCode);
            }

            string unknownMessage = _canSendUnknownErrorMessage(info)
                ? $"server error: {ex.Message}"
                : "server error";
            return RawResponse.ServerError(unknownMessage, 500);
        }
    }

    private static JsonNode? ParseJsonOrNull(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static RawResponse ServeStudio(string jsUrl) => RawResponse.OkHtml(StudioHtml(jsUrl));

    private static string StudioHtml(string jsUrl)
    {
        string safe = HtmlEscapeAttr(jsUrl);
        return $"<!DOCTYPE html><html>\n  <head>\n    <meta charset=\"utf-8\" />\n    <title>RPC Studio</title>\n    <link rel=\"icon\" href=\"data:image/svg+xml,<svg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 100 100%22><text y=%22.9em%22 font-size=%2290%22>%F0%9F%90%99</text></svg>\">\n    <script src=\"{safe}\"></script>\n  </head>\n  <body style=\"margin: 0; padding: 0;\">\n    <skir-studio-app></skir-studio-app>\n  </body>\n</html>";
    }

    private static string HtmlEscapeAttr(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&#34;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string HttpStatusText(ushort code) =>
        code switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            422 => "Unprocessable Entity",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            _ => "Error",
        };

    private const string DefaultStudioAppJsUrl =
        "https://cdn.jsdelivr.net/npm/skir-studio/dist/skir-studio-standalone.js";

    public static ServiceBuilder<TMeta> Builder() => new();

    internal static string DefaultStudioUrl => DefaultStudioAppJsUrl;
}

public sealed class ServiceBuilder<TMeta>
{
    private bool _keepUnrecognizedValues;
    private Func<MethodErrorInfo<TMeta>, bool> _canSendUnknownErrorMessage = _ => false;
    private Action<MethodErrorInfo<TMeta>> _errorLogger =
        info => Console.Error.WriteLine($"skir: error in method \"{info.MethodName}\": {info.Error.Message}");
    private string _studioAppJsUrl = Service<TMeta>.DefaultStudioUrl;
    private readonly Dictionary<long, MethodEntry<TMeta>> _byNum = [];
    private readonly Dictionary<string, long> _byName = [];

    public ServiceBuilder<TMeta> AddMethod<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        Func<TRequest, TMeta, Task<TResponse>> impl)
    {
        long number = method.Number;
        if (_byNum.ContainsKey(number))
            throw new InvalidOperationException($"skir: method number {number} already registered");

        var entry = new MethodEntry<TMeta>(
            method.Name,
            number,
            method.Doc,
            method.RequestSerializer.TypeDescriptor.AsJson(),
            method.ResponseSerializer.TypeDescriptor.AsJson(),
            async (requestJson, keepUnrecognized, readable, meta) =>
            {
                TRequest req;
                try
                {
                    req = method.RequestSerializer.FromJson(
                        requestJson,
                        keepUnrecognizedValues: keepUnrecognized);
                }
                catch (Exception ex)
                {
                    throw new ServiceError(
                        HttpErrorCode._400_BadRequest,
                        $"bad request: can't parse JSON: {ex.Message}");
                }

                TResponse resp = await impl(req, meta).ConfigureAwait(false);
                return method.ResponseSerializer.ToJson(resp, readable: readable);
            });

        _byName[method.Name] = number;
        _byNum[number] = entry;
        return this;
    }

    public ServiceBuilder<TMeta> SetKeepUnrecognizedValues(bool keep)
    {
        _keepUnrecognizedValues = keep;
        return this;
    }

    public ServiceBuilder<TMeta> SetCanSendUnknownErrorMessage(bool can)
    {
        _canSendUnknownErrorMessage = _ => can;
        return this;
    }

    public ServiceBuilder<TMeta> SetCanSendUnknownErrorMessageFn(Func<MethodErrorInfo<TMeta>, bool> predicate)
    {
        _canSendUnknownErrorMessage = predicate;
        return this;
    }

    public ServiceBuilder<TMeta> SetErrorLogger(Action<MethodErrorInfo<TMeta>> logger)
    {
        _errorLogger = logger;
        return this;
    }

    public ServiceBuilder<TMeta> SetStudioAppJsUrl(string url)
    {
        _studioAppJsUrl = url;
        return this;
    }

    public Service<TMeta> Build() =>
        new(
            _keepUnrecognizedValues,
            _canSendUnknownErrorMessage,
            _errorLogger,
            _studioAppJsUrl,
            _byNum,
            _byName);
}

internal sealed record MethodEntry<TMeta>(
    string Name,
    long Number,
    string Doc,
    string RequestTypeDescriptorJson,
    string ResponseTypeDescriptorJson,
    Func<string, bool, bool, TMeta, Task<string>> Invoke);
