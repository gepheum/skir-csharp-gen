namespace SkirClient;

/// <summary>
/// Represents a service method declared with the <c>method</c> keyword in the
/// .skir file. Carries metadata (name, number, documentation) and the
/// request/response serializers needed for routing and encoding RPC calls.
/// </summary>
public sealed record Method<TRequest, TResponse>(
	string Name,
	int Number,
	string Doc,
	Serializer<TRequest> RequestSerializer,
	Serializer<TResponse> ResponseSerializer);
