using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class DurableTaskStoreTests
{
    // Test adapter: an operation journal recreates the reference store after disposal. This is
    // deliberately single-writer; production hosts supply their own transactional durable store.
    private sealed class JournalStore : ITaskStore
    {
        private readonly string _path;
        private readonly object _sync = new();
        private readonly InMemoryTaskStore _inner;
        private readonly Dictionary<string, string> _ids = new();
        private readonly List<Operation> _journal;
        private DateTimeOffset _clock;
        public JournalStore(string path)
        {
            _path = path;
            _inner = new InMemoryTaskStore(() => _clock);
            _journal = File.Exists(path) ? JsonSerializer.Deserialize<List<Operation>>(File.ReadAllText(path), McpJsonDefaults.Options)! : [];
            foreach (var operation in _journal) Apply(operation);
        }
        private sealed record Operation
        {
            public string Kind { get; init; } = "";
            public string Id { get; init; } = "";
            public string? Owner { get; init; }
            public TaskMetadata? Metadata { get; init; }
            public JsonElement? Result { get; init; }
            public JsonRpcError? Error { get; init; }
            public McpTaskStatus Status { get; init; }
            public string? Message { get; init; }
            public DateTimeOffset At { get; init; }
        }
        private bool Apply(Operation op)
        {
            _clock = op.At;
            if (op.Kind == "create")
            {
                _ids[op.Id] = _inner.Create(op.Metadata, op.Owner).TaskId;
                return true;
            }
            if (!_ids.TryGetValue(op.Id, out var id)) return false;
            return op.Kind switch
            {
                "result" => _inner.SetResult(id, op.Result!.Value),
                "error" => _inner.SetError(id, op.Error!),
                "status" => _inner.UpdateStatus(id, op.Status, op.Message),
                "cancel" => _inner.Cancel(id, op.Owner) is not null,
                _ => throw new InvalidDataException("Unknown journal operation.")
            };
        }
        private bool Write(Operation operation)
        {
            lock (_sync)
            {
                operation = operation with { At = DateTimeOffset.UtcNow };
                if (!Apply(operation)) return false;
                _journal.Add(operation);
                File.WriteAllText(_path + ".next", JsonSerializer.Serialize(_journal, McpJsonDefaults.Options));
                File.Move(_path + ".next", _path, overwrite: true);
                return true;
            }
        }
        public McpTask Create(TaskMetadata? metadata, string? ownerKey)
        {
            lock (_sync)
            {
                var id = Guid.NewGuid().ToString("N");
                Write(new Operation { Kind = "create", Id = id, Owner = ownerKey, Metadata = metadata });
                return Get(id, ownerKey)!;
            }
        }
        public McpTask? Get(string taskId, string? ownerKey)
        {
            lock (_sync)
            {
                _clock = DateTimeOffset.UtcNow;
                return _ids.TryGetValue(taskId, out var id) && _inner.Get(id, ownerKey) is { } task ? task with { TaskId = taskId } : null;
            }
        }
        public IReadOnlyList<McpTask> List(string? ownerKey)
        {
            lock (_sync) return _ids.Keys.Select(id => Get(id, ownerKey)).OfType<McpTask>().ToArray();
        }
        public McpTask? Cancel(string taskId, string? ownerKey) => Write(new Operation { Kind = "cancel", Id = taskId, Owner = ownerKey }) ? Get(taskId, ownerKey) : null;
        public bool SetResult(string taskId, JsonElement result) => Write(new Operation { Kind = "result", Id = taskId, Result = result.Clone() });
        public bool SetError(string taskId, JsonRpcError error) => Write(new Operation { Kind = "error", Id = taskId, Error = error });
        public bool SetFailed(string taskId, string? statusMessage = null) => SetError(taskId, JsonRpcError.InternalError(statusMessage));
        public bool UpdateStatus(string taskId, McpTaskStatus status, string? statusMessage = null) => Write(new Operation { Kind = "status", Id = taskId, Status = status, Message = statusMessage });
        public JsonElement? GetResult(string taskId, string? ownerKey)
        {
            lock (_sync) return Get(taskId, ownerKey) is null ? null : _inner.GetResult(_ids[taskId], ownerKey);
        }
        public JsonRpcError? GetError(string taskId, string? ownerKey)
        {
            lock (_sync) return Get(taskId, ownerKey) is null ? null : _inner.GetError(_ids[taskId], ownerKey);
        }
    }

    [Fact]
    public async Task ToolResult_SurvivesNewStoreAndConnection_WithoutReexecution()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tasks.json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string id;
            var (ct, st) = InMemoryTransport.CreatePair();
            await using (var server = new McpServer(st, new McpServerOptions { TaskStore = new JournalStore(path), TaskOwnerKey = "trusted-owner" }))
            {
                server.AddTool("job", "job", (_, _) => Task.FromResult(CallToolResult.Text("persisted")));
                var run = server.RunAsync(timeout.Token);
                await using var client = await McpClient.ConnectAsync(ct, cancellationToken: timeout.Token);
                id = (await client.CallToolAsTaskAsync("job", ttlMs: 60_000, ct: timeout.Token)).Task.TaskId;
                await client.GetTaskResultAsync(id, timeout.Token);
            }
            var recovered = new JournalStore(path);
            Assert.Empty(recovered.List("other-owner"));
            Assert.Null(recovered.GetResult(id, "other-owner"));
            var (ct2, st2) = InMemoryTransport.CreatePair();
            await using var server2 = new McpServer(st2, new McpServerOptions { TaskStore = recovered, TaskOwnerKey = "trusted-owner" });
            var run2 = server2.RunAsync(timeout.Token);
            await using var client2 = await McpClient.ConnectAsync(ct2, cancellationToken: timeout.Token);
            var payload = await client2.GetTaskResultAsync(id, timeout.Token);
            Assert.Equal("persisted", payload.Deserialize<CallToolResult>(McpJsonDefaults.Options)!.Content.OfType<TextContent>().Single().Text);
            Assert.Equal(id, Assert.Single(await client2.ListTasksAsync(timeout.Token)).TaskId);
        }
        finally { File.Delete(path); File.Delete(path + ".next"); }
    }

    [Fact]
    public async Task RpcError_SurvivesStoreRecreation_WithOriginalData()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tasks.json");
        try
        {
            var original = new JournalStore(path);
            var task = original.Create(new TaskMetadata { Ttl = 60_000 }, "owner");
            original.SetError(task.TaskId, new JsonRpcError { Code = -32099, Message = "stored error", Data = McpJsonDefaults.ToElement(new { retry = 17 }) });
            var recovered = new JournalStore(path);
            var response = await TaskResults.WaitAsync(recovered, "owner", new JsonRpcRequest
            {
                Id = 1,
                Method = McpMethods.TasksResult,
                Params = McpJsonDefaults.ToElement(new { taskId = task.TaskId })
            }, default);
            Assert.Equal(-32099, response.Error!.Code);
            Assert.Equal(17, response.Error.Data!.Value.GetProperty("retry").GetInt32());
            Assert.Null(recovered.GetError(task.TaskId, "other"));
        }
        finally { File.Delete(path); File.Delete(path + ".next"); }
    }
    private sealed class AcceptInput : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) => Task.FromResult(ElicitResult.Accept(McpJsonDefaults.ToElement(new { answer = 42 })));
    }

    [Fact]
    public async Task ClientResult_SurvivesNewStoreAndConnection()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tasks.json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string id;
            var (ct, st) = InMemoryTransport.CreatePair();
            await using (var server = new McpServer(st))
            {
                var run = server.RunAsync(timeout.Token);
                await using var client = await McpClient.ConnectAsync(ct, new McpClientOptions
                {
                    TaskStore = new JournalStore(path),
                    TaskOwnerKey = "trusted-owner",
                    ElicitationHandler = new AcceptInput()
                }, cancellationToken: timeout.Token);
                var request = ElicitRequest.Form("input", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() });
                id = (await server.ElicitAsTaskAsync(request, ttlMs: 60_000, cancellationToken: timeout.Token)).Task.TaskId;
                await server.GetClientTaskResultAsync(id, timeout.Token);
            }
            var (ct2, st2) = InMemoryTransport.CreatePair();
            await using var server2 = new McpServer(st2);
            var run2 = server2.RunAsync(timeout.Token);
            await using var client2 = await McpClient.ConnectAsync(ct2, new McpClientOptions
            {
                TaskStore = new JournalStore(path),
                TaskOwnerKey = "trusted-owner"
            }, cancellationToken: timeout.Token);
            var result = await server2.GetClientTaskResultAsync(id, timeout.Token);
            Assert.Equal("accept", result.GetProperty("action").GetString());
            Assert.Equal(42, result.GetProperty("content").GetProperty("answer").GetInt32());
        }
        finally { File.Delete(path); File.Delete(path + ".next"); }
    }

}
