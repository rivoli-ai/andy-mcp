using Andy.MCP.Protocol;
namespace Andy.MCP.Transport;

/// <summary>Optional transport routing for messages produced while handling an inbound request.</summary>
public interface IRequestContextTransport
{
    /// <summary>Enter a scope that associates nested requests and notifications with the originating request.</summary>
    IDisposable EnterRequestScope(RequestId requestId);
}
