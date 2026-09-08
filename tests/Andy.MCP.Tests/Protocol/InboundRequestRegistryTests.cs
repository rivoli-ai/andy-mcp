using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class InboundRequestRegistryTests
{
    [Fact]
    public async Task RepeatedShutdown_DrainsConcurrentHandlersAndRejectsNewWork()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var registry = new InboundRequestRegistry();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            var exited = 0;
            for (var i = 0; i < 10; i++)
                Assert.True(registry.Run(i, CancellationToken.None, async ct =>
                {
                    try
                    {
                        if (Interlocked.Increment(ref entered) == 10) started.SetResult();
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException) { }
                    finally { Interlocked.Increment(ref exited); }
                }));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(registry.StopAsync(), registry.StopAsync());
            Assert.Equal(10, exited);
            Assert.Equal(0, registry.Count);
            Assert.False(registry.Run(99, CancellationToken.None, _ => Task.CompletedTask));
        }
    }
}
