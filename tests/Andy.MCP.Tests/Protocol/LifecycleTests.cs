using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class LifecycleTypesTests
{
    [Fact]
    public void InitializeParams_RoundTrips()
    {
        var p = new InitializeParams
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ClientCapabilities
            {
                Roots = new RootsCapability { ListChanged = true },
                Sampling = new SamplingCapability()
            },
            ClientInfo = new Implementation("TestClient", "1.0.0")
        };

        var json = JsonSerializer.Serialize(p, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<InitializeParams>(json, McpJsonDefaults.Options)!;

        Assert.Equal("2025-06-18", deserialized.ProtocolVersion);
        Assert.Equal("TestClient", deserialized.ClientInfo.Name);
        Assert.Equal("1.0.0", deserialized.ClientInfo.Version);
        Assert.True(deserialized.Capabilities.Roots?.ListChanged);
        Assert.NotNull(deserialized.Capabilities.Sampling);
    }

    [Fact]
    public void InitializeResult_RoundTrips()
    {
        var r = new InitializeResult
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ServerCapabilities
            {
                Tools = new ListChangedCapability { ListChanged = true },
                Resources = new ResourcesCapability { Subscribe = true, ListChanged = true },
                Logging = new EmptyCapability()
            },
            ServerInfo = new Implementation("TestServer", "2.0.0") { Title = "Test Server" },
            Instructions = "Use this server to get weather data."
        };

        var json = JsonSerializer.Serialize(r, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<InitializeResult>(json, McpJsonDefaults.Options)!;

        Assert.Equal("2025-06-18", deserialized.ProtocolVersion);
        Assert.Equal("TestServer", deserialized.ServerInfo.Name);
        Assert.Equal("Test Server", deserialized.ServerInfo.Title);
        Assert.True(deserialized.Capabilities.Tools?.ListChanged);
        Assert.True(deserialized.Capabilities.Resources?.Subscribe);
        Assert.True(deserialized.Capabilities.Resources?.ListChanged);
        Assert.NotNull(deserialized.Capabilities.Logging);
        Assert.Null(deserialized.Capabilities.Prompts);
        Assert.Equal("Use this server to get weather data.", deserialized.Instructions);
    }

    [Fact]
    public void InitializeResult_NullInstructions_OmittedInJson()
    {
        var r = new InitializeResult
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ServerCapabilities(),
            ServerInfo = new Implementation("S", "1.0")
        };

        var json = JsonSerializer.Serialize(r, McpJsonDefaults.Options);
        Assert.DoesNotContain("instructions", json);
    }

    [Fact]
    public void ClientCapabilities_Empty_SerializesAsEmptyObject()
    {
        var caps = new ClientCapabilities();
        var json = JsonSerializer.Serialize(caps, McpJsonDefaults.Options);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void ServerCapabilities_Empty_SerializesAsEmptyObject()
    {
        var caps = new ServerCapabilities();
        var json = JsonSerializer.Serialize(caps, McpJsonDefaults.Options);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void ClientCapabilities_Elicitation()
    {
        var caps = new ClientCapabilities { Elicitation = new EmptyCapability() };
        var json = JsonSerializer.Serialize(caps, McpJsonDefaults.Options);
        Assert.Contains("\"elicitation\":{}", json);
    }

    [Fact]
    public void ServerCapabilities_Completions()
    {
        var caps = new ServerCapabilities { Completions = new EmptyCapability() };
        var json = JsonSerializer.Serialize(caps, McpJsonDefaults.Options);
        Assert.Contains("\"completions\":{}", json);
    }

    [Fact]
    public void Experimental_Capabilities_RoundTrip()
    {
        var caps = new ClientCapabilities
        {
            Experimental = new Dictionary<string, JsonElement>
            {
                ["customFeature"] = JsonSerializer.SerializeToElement(new { enabled = true, version = 2 })
            }
        };

        var json = JsonSerializer.Serialize(caps, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ClientCapabilities>(json, McpJsonDefaults.Options)!;

        Assert.NotNull(deserialized.Experimental);
        Assert.True(deserialized.Experimental!["customFeature"].GetProperty("enabled").GetBoolean());
        Assert.Equal(2, deserialized.Experimental["customFeature"].GetProperty("version").GetInt32());
    }

    [Fact]
    public void Implementation_WithTitle()
    {
        var impl = new Implementation("MyApp", "1.0.0") { Title = "My Application" };
        var json = JsonSerializer.Serialize(impl, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Implementation>(json, McpJsonDefaults.Options)!;

        Assert.Equal("MyApp", deserialized.Name);
        Assert.Equal("1.0.0", deserialized.Version);
        Assert.Equal("My Application", deserialized.Title);
    }

    [Fact]
    public void Implementation_WithoutTitle_OmitsField()
    {
        var impl = new Implementation("MyApp", "1.0.0");
        var json = JsonSerializer.Serialize(impl, McpJsonDefaults.Options);
        Assert.DoesNotContain("title", json);
    }

    [Fact]
    public void FullInitializeHandshake_InJsonRpcMessages()
    {
        // Client sends initialize request
        var initRequest = new JsonRpcRequest
        {
            Id = 1,
            Method = McpMethods.Initialize,
            Params = McpJsonDefaults.ToElement(new InitializeParams
            {
                ProtocolVersion = McpSession.LatestProtocolVersion,
                Capabilities = new ClientCapabilities
                {
                    Roots = new RootsCapability { ListChanged = true },
                    Sampling = new SamplingCapability()
                },
                ClientInfo = new Implementation("TestClient", "1.0.0")
            })
        };

        var reqJson = McpJsonDefaults.Serialize(initRequest);
        var parsedReq = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(reqJson));
        Assert.Equal("initialize", parsedReq.Method);

        var reqParams = parsedReq.GetParams<InitializeParams>()!;
        Assert.Equal(McpSession.LatestProtocolVersion, reqParams.ProtocolVersion);

        // Server sends initialize response
        var initResponse = JsonRpcResponse.Success(
            (RequestId)1,
            McpJsonDefaults.ToElement(new InitializeResult
            {
                ProtocolVersion = "2025-06-18",
                Capabilities = new ServerCapabilities
                {
                    Tools = new ListChangedCapability { ListChanged = true }
                },
                ServerInfo = new Implementation("TestServer", "2.0.0"),
                Instructions = "I provide tools."
            }));

        var respJson = McpJsonDefaults.Serialize(initResponse);
        var parsedResp = Assert.IsType<JsonRpcResponse>(McpJsonDefaults.Deserialize(respJson));
        var result = parsedResp.GetResult<InitializeResult>()!;
        Assert.Equal("2025-06-18", result.ProtocolVersion);
        Assert.NotNull(result.Capabilities.Tools);
        Assert.Equal("I provide tools.", result.Instructions);

        // Client sends initialized notification
        var initializedNotif = new JsonRpcNotification { Method = McpMethods.NotificationsInitialized };
        var notifJson = McpJsonDefaults.Serialize(initializedNotif);
        var parsedNotif = Assert.IsType<JsonRpcNotification>(McpJsonDefaults.Deserialize(notifJson));
        Assert.Equal("notifications/initialized", parsedNotif.Method);
    }
}

public class McpSessionTests
{
    #region State Machine

    [Fact]
    public void InitialState_IsUninitialized()
    {
        var session = new McpSession();
        Assert.Equal(McpSessionState.Uninitialized, session.State);
    }

    [Fact]
    public void ValidTransition_Uninitialized_To_Initializing()
    {
        var session = new McpSession();
        Assert.True(session.TryTransition(McpSessionState.Initializing));
        Assert.Equal(McpSessionState.Initializing, session.State);
    }

    [Fact]
    public void ValidTransition_Initializing_To_Ready()
    {
        var session = new McpSession();
        session.Transition(McpSessionState.Initializing);
        Assert.True(session.TryTransition(McpSessionState.Ready));
        Assert.Equal(McpSessionState.Ready, session.State);
    }

    [Fact]
    public void ValidTransition_Ready_To_ShuttingDown()
    {
        var session = CreateReadySession();
        Assert.True(session.TryTransition(McpSessionState.ShuttingDown));
    }

    [Fact]
    public void ValidTransition_ShuttingDown_To_Closed()
    {
        var session = CreateReadySession();
        session.Transition(McpSessionState.ShuttingDown);
        Assert.True(session.TryTransition(McpSessionState.Closed));
    }

    [Fact]
    public void ValidTransition_Ready_To_Closed_Abrupt()
    {
        var session = CreateReadySession();
        Assert.True(session.TryTransition(McpSessionState.Closed));
    }

    [Fact]
    public void ValidTransition_Initializing_To_Closed_FailedInit()
    {
        var session = new McpSession();
        session.Transition(McpSessionState.Initializing);
        Assert.True(session.TryTransition(McpSessionState.Closed));
    }

    [Fact]
    public void InvalidTransition_Uninitialized_To_Ready()
    {
        var session = new McpSession();
        Assert.False(session.TryTransition(McpSessionState.Ready));
        Assert.Equal(McpSessionState.Uninitialized, session.State);
    }

    [Fact]
    public void InvalidTransition_Closed_To_Ready()
    {
        var session = CreateReadySession();
        session.Transition(McpSessionState.Closed);

        Assert.Throws<McpSessionException>(() => session.Transition(McpSessionState.Ready));
    }

    [Fact]
    public void InvalidTransition_Ready_To_Initializing()
    {
        var session = CreateReadySession();
        Assert.False(session.TryTransition(McpSessionState.Initializing));
    }

    [Fact]
    public void Transition_Throws_OnInvalidTransition()
    {
        var session = new McpSession();
        var ex = Assert.Throws<McpSessionException>(() => session.Transition(McpSessionState.Closed));
        Assert.Contains("Invalid state transition", ex.Message);
    }

    #endregion

    #region Thread Safety

    [Fact]
    public void ConcurrentTransitions_OnlyOneSucceeds()
    {
        // Multiple threads try to transition from Uninitialized to Initializing
        var session = new McpSession();
        int successCount = 0;

        Parallel.For(0, 100, _ =>
        {
            if (session.TryTransition(McpSessionState.Initializing))
                Interlocked.Increment(ref successCount);
        });

        Assert.Equal(1, successCount);
        Assert.Equal(McpSessionState.Initializing, session.State);
    }

    #endregion

    #region Initialization

    [Fact]
    public void CompleteInitializationAsClient_StoresServerInfo()
    {
        var session = new McpSession();
        session.Transition(McpSessionState.Initializing);

        session.CompleteInitializationAsClient(new InitializeResult
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ServerCapabilities
            {
                Tools = new ListChangedCapability { ListChanged = true },
                Resources = new ResourcesCapability { Subscribe = true }
            },
            ServerInfo = new Implementation("TestServer", "1.0.0"),
            Instructions = "Use tools wisely."
        });

        Assert.Equal(McpSessionState.Ready, session.State);
        Assert.Equal("2025-06-18", session.ProtocolVersion);
        Assert.Equal("TestServer", session.RemoteInfo?.Name);
        Assert.Equal("Use tools wisely.", session.Instructions);
        Assert.NotNull(session.ServerCapabilities?.Tools);
        Assert.NotNull(session.ServerCapabilities?.Resources);
    }

    [Fact]
    public void CompleteInitializationAsServer_StoresClientInfo()
    {
        var session = new McpSession();
        session.Transition(McpSessionState.Initializing);

        var clientParams = new InitializeParams
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ClientCapabilities
            {
                Roots = new RootsCapability { ListChanged = true },
                Sampling = new SamplingCapability()
            },
            ClientInfo = new Implementation("TestClient", "2.0.0")
        };

        session.CompleteInitializationAsServer(clientParams, "2025-06-18");

        Assert.Equal(McpSessionState.Ready, session.State);
        Assert.Equal("2025-06-18", session.ProtocolVersion);
        Assert.Equal("TestClient", session.RemoteInfo?.Name);
        Assert.NotNull(session.ClientCapabilities?.Roots);
        Assert.NotNull(session.ClientCapabilities?.Sampling);
    }

    #endregion

    #region Capabilities

    [Fact]
    public void HasServerCapability_ReturnsTrueWhenNegotiated()
    {
        var session = CreateReadySession(tools: true, resources: true);

        Assert.True(session.HasServerCapability("tools"));
        Assert.True(session.HasServerCapability("resources"));
    }

    [Fact]
    public void HasServerCapability_ReturnsFalseWhenNotNegotiated()
    {
        var session = CreateReadySession(tools: true);

        Assert.False(session.HasServerCapability("prompts"));
        Assert.False(session.HasServerCapability("logging"));
        Assert.False(session.HasServerCapability("completions"));
    }

    [Fact]
    public void RequireServerCapability_ThrowsWhenNotAvailable()
    {
        var session = CreateReadySession(tools: true);

        var ex = Assert.Throws<McpCapabilityNotAvailableException>(() =>
            session.RequireServerCapability("prompts"));
        Assert.Equal("prompts", ex.CapabilityName);
        Assert.Contains("not available", ex.Message);
    }

    [Fact]
    public void RequireServerCapability_DoesNotThrowWhenAvailable()
    {
        var session = CreateReadySession(tools: true);
        session.RequireServerCapability("tools"); // No exception
    }

    [Fact]
    public void HasClientCapability_Works()
    {
        var session = new McpSession();
        session.Transition(McpSessionState.Initializing);
        session.CompleteInitializationAsServer(new InitializeParams
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ClientCapabilities
            {
                Roots = new RootsCapability(),
                Sampling = new SamplingCapability()
            },
            ClientInfo = new Implementation("C", "1.0")
        }, "2025-06-18");

        Assert.True(session.HasClientCapability("roots"));
        Assert.True(session.HasClientCapability("sampling"));
        Assert.False(session.HasClientCapability("elicitation"));
    }

    #endregion

    #region Version Negotiation

    [Fact]
    public void NegotiateVersion_MatchingVersion()
    {
        Assert.Equal("2025-06-18", McpSession.NegotiateVersion("2025-06-18"));
    }

    [Fact]
    public void NegotiateVersion_OlderSupportedVersion()
    {
        Assert.Equal("2024-11-05", McpSession.NegotiateVersion("2024-11-05"));
    }

    [Fact]
    public void NegotiateVersion_UnknownVersion_ReturnsLatest()
    {
        Assert.Equal(McpSession.LatestProtocolVersion, McpSession.NegotiateVersion("2099-01-01"));
    }

    [Fact]
    public void IsVersionAcceptable_SupportedVersion()
    {
        Assert.True(McpSession.IsVersionAcceptable("2025-06-18"));
        Assert.True(McpSession.IsVersionAcceptable("2025-03-26"));
        Assert.True(McpSession.IsVersionAcceptable("2024-11-05"));
    }

    [Fact]
    public void IsVersionAcceptable_UnsupportedVersion()
    {
        Assert.False(McpSession.IsVersionAcceptable("2099-01-01"));
        Assert.False(McpSession.IsVersionAcceptable("1.0"));
    }

    #endregion

    private static McpSession CreateReadySession(bool tools = false, bool resources = false)
    {
        var session = new McpSession();
        session.Transition(McpSessionState.Initializing);
        session.CompleteInitializationAsClient(new InitializeResult
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ServerCapabilities
            {
                Tools = tools ? new ListChangedCapability { ListChanged = true } : null,
                Resources = resources ? new ResourcesCapability { Subscribe = true } : null,
            },
            ServerInfo = new Implementation("TestServer", "1.0.0")
        });
        return session;
    }
}
