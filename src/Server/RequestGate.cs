namespace PlayniteBridge.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Bounds and de-duplicates inbound HTTP work.
    /// - A semaphore caps concurrent handler executions (memory/threads stay flat).
    /// - Identical concurrent GETs share one execution (fling storms collapse).
    /// Writes (POST/PUT/DELETE) always execute individually.
    /// </summary>
    internal class RequestGate : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _flight =
            new ConcurrentDictionary<string, Lazy<Task<object>>>();
        private const int MaxFlightEntries = 512;
        private bool _disposed;

        public RequestGate(int maxConcurrent = 12)
        {
            _sem = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }

        public object Execute(RequestContext ctx, Func<object> handler)
        {
            if (ctx == null || !"GET".Equals(ctx.Method, StringComparison.OrdinalIgnoreCase) || _flight.Count > MaxFlightEntries)
                return RunLimited(handler);

            var key = (ctx.Method ?? "GET") + " " + (ctx.RawUrl ?? ctx.Path ?? "/");
            var lazy = _flight.GetOrAdd(key, _ => new Lazy<Task<object>>(
                () => Task.Run(() => RunLimited(handler)),
                LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                return lazy.Value.GetAwaiter().GetResult();
            }
            finally
            {
                // Keep the completed entry briefly: requests arriving right
                // behind (fling bursts) share it instead of redoing the work.
                Task.Delay(500).ContinueWith(_ =>
                {
                    Lazy<Task<object>> existing;
                    if (_flight.TryGetValue(key, out existing) && ReferenceEquals(existing, lazy))
                        _flight.TryRemove(key, out existing);
                });
            }
        }

        private object RunLimited(Func<object> handler)
        {
            _sem.Wait();
            try { return handler(); }
            finally { _sem.Release(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sem.Dispose();
        }
    }
}
