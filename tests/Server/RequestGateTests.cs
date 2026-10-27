namespace PlayniteBridge.Tests.Server
{
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using PlayniteBridge.Server;

    [TestFixture]
    public class RequestGateTests
    {
        private static RequestContext Get(string method, string rawUrl)
        {
            return new RequestContext { Method = method, Path = rawUrl, RawUrl = rawUrl };
        }

        [Test]
        public void ConcurrentIdenticalGets_ShareOneExecution()
        {
            var gate = new RequestGate(maxConcurrent: 8);
            var executions = 0;
            var results = new System.Collections.Concurrent.ConcurrentBag<object>();

            Parallel.For(0, 16, _ =>
            {
                results.Add(gate.Execute(Get("GET", "/api/games?limit=10"), () =>
                {
                    Interlocked.Increment(ref executions);
                    Thread.Sleep(50);
                    return (object)"ok";
                }));
            });

            gate.Dispose();
            Assert.That(executions, Is.EqualTo(1));
            Assert.That(results.Count, Is.EqualTo(16));
            Assert.That(results.All(r => Equals(r, "ok")), Is.True);
        }

        [Test]
        public void DifferentUrls_ExecuteIndependently()
        {
            var gate = new RequestGate(maxConcurrent: 8);
            var executions = 0;

            Parallel.For(0, 8, i =>
            {
                gate.Execute(Get("GET", "/api/games/" + i), () =>
                {
                    Interlocked.Increment(ref executions);
                    return (object)i;
                });
            });

            gate.Dispose();
            Assert.That(executions, Is.EqualTo(8));
        }

        [Test]
        public void Writes_BypassSingleFlight()
        {
            var gate = new RequestGate(maxConcurrent: 8);
            var executions = 0;

            Parallel.For(0, 8, _ =>
            {
                gate.Execute(Get("POST", "/api/games"), () =>
                {
                    Interlocked.Increment(ref executions);
                    return (object)"ok";
                });
            });

            gate.Dispose();
            Assert.That(executions, Is.EqualTo(8));
        }

        [Test]
        public void HandlerExceptions_PropagateToCaller()
        {
            var gate = new RequestGate(maxConcurrent: 8);
            Assert.Throws<System.InvalidOperationException>(() =>
                gate.Execute(Get("GET", "/api/games"), () => throw new System.InvalidOperationException("boom")));
            gate.Dispose();
        }
    }
}
