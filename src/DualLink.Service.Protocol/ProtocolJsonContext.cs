using System.Text.Json.Serialization;

namespace DualLink.Service.Protocol;

[JsonSerializable(typeof(ServiceRequest))]
[JsonSerializable(typeof(ServiceResponse))]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(HelloResponse))]
[JsonSerializable(typeof(StartSessionRequest))]
[JsonSerializable(typeof(UpdateTargetsRequest))]
[JsonSerializable(typeof(UpdateRoutesRequest))]
[JsonSerializable(typeof(SessionStatus))]
[JsonSerializable(typeof(RouteDefinition))]
[JsonSerializable(typeof(RouteStatus))]
[JsonSerializable(typeof(RoutingMode))]
public partial class ProtocolJsonContext : JsonSerializerContext;
