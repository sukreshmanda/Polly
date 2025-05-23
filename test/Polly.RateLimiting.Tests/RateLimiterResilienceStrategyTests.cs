using System.Threading.RateLimiting;
using NSubstitute;
using Polly.TestUtils;

namespace Polly.RateLimiting.Tests;

public class RateLimiterResilienceStrategyTests
{
    private readonly RateLimiter _limiter = Substitute.For<RateLimiter>();
    private readonly RateLimitLease _lease = Substitute.For<RateLimitLease>();
    private readonly FakeTelemetryListener _listener = new();
    private Func<OnRateLimiterRejectedArguments, ValueTask>? _event;

    [Fact]
    public void Ctor_Ok() =>
        Create().ShouldNotBeNull();

    [Fact]
    public async Task Execute_HappyPath()
    {
        using var cts = new CancellationTokenSource();

        SetupLimiter(cts.Token);

        _lease.IsAcquired.Returns(true);

        Create().ShouldNotBeNull();

        var strategy = Create();

        strategy.Execute(_ => { }, cts.Token);

        await _limiter.ReceivedWithAnyArgs().AcquireAsync(default, default);
        _lease.Received().Dispose();
    }

    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [Theory]
    public async Task Execute_LeaseRejected(bool hasEvents, bool hasRetryAfter)
    {
        object? metadata = hasRetryAfter ? TimeSpan.FromSeconds(123) : null;

        using var cts = new CancellationTokenSource();
        var eventCalled = false;
        SetupLimiter(cts.Token);

        _lease.IsAcquired.Returns(false);
        _lease.TryGetMetadata("RETRY_AFTER", out Arg.Any<object?>())
              .Returns(x =>
              {
                  x[1] = hasRetryAfter ? metadata : null;
                  return hasRetryAfter;
              });

        if (hasEvents)
        {
            _event = args =>
            {
                args.Context.ShouldNotBeNull();
                args.Lease.ShouldBe(_lease);
                eventCalled = true;
                return default;
            };
        }

        var strategy = Create();
        var context = ResilienceContextPool.Shared.Get(cts.Token);
        var outcome = await strategy.ExecuteOutcomeAsync((_, _) => Outcome.FromResultAsValueTask("dummy"), context, "state");

        outcome.Exception.ShouldNotBeNull();

        RateLimiterRejectedException exception = outcome.Exception
            .ShouldBeOfType<RateLimiterRejectedException>();

        exception.RetryAfter.ShouldBe((TimeSpan?)metadata);
        exception.StackTrace.ShouldNotBeNull();
        exception.StackTrace.ShouldContain("Execute_LeaseRejected");
        exception.TelemetrySource.ShouldNotBeNull();

        eventCalled.ShouldBe(hasEvents);

        await _limiter.ReceivedWithAnyArgs().AcquireAsync(default, default);
        _lease.Received().Dispose();

        _listener.GetArgs<OnRateLimiterRejectedArguments>().Count().ShouldBe(1);
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task Dispose_DisposableResourcesShouldBeDisposed(bool isAsync)
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1 });
        using var wrapper = new DisposeWrapper(limiter);
        var strategy = new RateLimiterResilienceStrategy(null!, null, null!, wrapper);

        if (isAsync)
        {
            await strategy.DisposeAsync();
        }
        else
        {
#pragma warning disable S6966
            strategy.Dispose();
#pragma warning restore S6966
        }

        await Should.ThrowAsync<ObjectDisposedException>(() => limiter.AcquireAsync(1).AsTask());
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task Dispose_NoDisposableResources_ShouldNotThrow(bool isAsync)
    {
        using var strategy = new RateLimiterResilienceStrategy(null!, null, null!, null);

        if (isAsync)
        {
            await Should.NotThrowAsync(() => strategy.DisposeAsync().AsTask());
        }
        else
        {
            Should.NotThrow(() => strategy.Dispose());
        }
    }

    private void SetupLimiter(CancellationToken token)
    {
        var result = new ValueTask<RateLimitLease>(_lease);
        _limiter
            .GetType()
            .GetMethod("AcquireAsyncCore", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_limiter, [1, token])
            .Returns(result);
    }

    private ResiliencePipeline Create()
    {
        var builder = new ResiliencePipelineBuilder
        {
            TelemetryListener = _listener
        };

        return builder.AddRateLimiter(new RateLimiterStrategyOptions
        {
            RateLimiter = args => _limiter.AcquireAsync(1, args.Context.CancellationToken),
            OnRejected = _event
        })
        .Build();
    }
}
