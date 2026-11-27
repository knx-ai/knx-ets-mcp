using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Knx.EtsBridge.Addin
{
    [TestFixture]
    public class MonitoredConnectionTests
    {
        private MonitoredConnection<FakeConnection> NewSession()
        {
            return new MonitoredConnection<FakeConnection>(new object());
        }

        [Test]
        public void WithoutMonitorUsesExclusiveFallback()
        {
            var session = NewSession();
            Assert.AreEqual(7, session.Use(_ => throw new Exception("No monitor exists."), () => 7));
        }

        [Test]
        public void MonitorReadBorrowsSameTransportWithoutAcquiringExclusiveBusAgain()
        {
            var session = NewSession();
            var connection = new FakeConnection();
            session.Attach("monitor", connection);
            var actual = session.Use(value => value, () => throw new Exception("Exclusive lease is held by monitor."));
            Assert.AreSame(connection, actual);
            Assert.IsFalse(connection.Disposed);
        }

        [Test]
        public void SecondAttachCannotReplaceActiveMonitor()
        {
            var session = NewSession();
            var connection = new FakeConnection();
            session.Attach("first", connection);
            Assert.Throws<InvalidOperationException>(() => session.Attach("second", new FakeConnection()));
            Assert.AreSame(connection, session.Use(value => value, () => new FakeConnection()));
        }

        [Test]
        public void OtherOwnerCannotCloseActiveMonitor()
        {
            var session = NewSession();
            var connection = new FakeConnection();
            session.Attach("first", connection);
            session.Close("other", _ => Assert.Fail("Wrong owner must not unsubscribe."));
            Assert.IsFalse(connection.Disposed);
            Assert.AreSame(connection, session.Use(value => value, () => new FakeConnection()));
        }

        [Test]
        public void CloseUnsubscribesBeforeDisposingAndThenUsesFallback()
        {
            var session = NewSession();
            var order = new List<string>();
            var connection = new FakeConnection { OnDispose = () => order.Add("dispose") };
            session.Attach("monitor", connection);
            session.Close("monitor", _ => order.Add("unsubscribe"));
            CollectionAssert.AreEqual(new[] { "unsubscribe", "dispose" }, order);
            Assert.AreEqual(7, session.Use(_ => -1, () => 7));
        }

        [Test]
        public void RepeatedCloseDoesNotDisposeTwice()
        {
            var session = NewSession();
            var connection = new FakeConnection();
            session.Attach("monitor", connection);
            session.Close("monitor", _ => { });
            session.Close("monitor", _ => Assert.Fail("Already closed."));
            Assert.AreEqual(1, connection.DisposeCount);
        }

        [Test]
        public void FailingIoDoesNotDisposeBorrowedConnection()
        {
            var session = NewSession();
            var connection = new FakeConnection();
            session.Attach("monitor", connection);
            Assert.Throws<InvalidOperationException>(() => session.Use<int>(
                _ => throw new InvalidOperationException("I/O failure"), () => 7));
            Assert.IsFalse(connection.Disposed);
            Assert.AreSame(connection, session.Use(value => value, () => new FakeConnection()));
        }

        [Test]
        public void FailingUnsubscribeStillDisposesAndDetaches()
        {
            var session = NewSession();
            var connection = new FakeConnection();
            session.Attach("monitor", connection);
            Assert.Throws<InvalidOperationException>(() => session.Close("monitor",
                _ => throw new InvalidOperationException("unsubscribe")));
            Assert.IsTrue(connection.Disposed);
            Assert.AreEqual(7, session.Use(_ => -1, () => 7));
        }

        [Test]
        public void FailingDisposeStillDetaches()
        {
            var session = NewSession();
            var connection = new FakeConnection { OnDispose = () => throw new InvalidOperationException("dispose") };
            session.Attach("monitor", connection);
            Assert.Throws<InvalidOperationException>(() => session.Close("monitor", _ => { }));
            Assert.AreEqual(7, session.Use(_ => -1, () => 7));
        }

        [Test]
        public void ObsoleteCloseCannotDisposeANewerMonitor()
        {
            var session = NewSession();
            session.Attach("old", new FakeConnection());
            session.Close("old", _ => { });
            var current = new FakeConnection();
            session.Attach("new", current);
            session.Close("old", _ => Assert.Fail("Obsolete owner."));
            Assert.IsFalse(current.Disposed);
            Assert.AreSame(current, session.Use(value => value, () => new FakeConnection()));
        }

        [Test]
        public void FallbackFailuresAreNotHidden()
        {
            var session = NewSession();
            Assert.Throws<InvalidOperationException>(() => session.Use(_ => 7,
                () => throw new InvalidOperationException("Programming owns the exclusive lease.")));
        }

        [Test]
        public void MonitorCannotBeDisposedDuringBorrowedIo()
        {
            var session = NewSession();
            var connection = new FakeConnection();
            session.Attach("monitor", connection);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var closeRequested = new ManualResetEventSlim();
            var io = Task.Run(() => session.Use(value =>
            {
                entered.Set();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)), "Test must release borrowed I/O.");
                Assert.IsFalse(value.Disposed);
                return 7;
            }, () => -1));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            var close = Task.Run(() =>
            {
                closeRequested.Set();
                session.Close("monitor", _ => { });
            });
            try
            {
                Assert.IsTrue(closeRequested.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(connection.Disposed);
            }
            finally
            {
                release.Set();
            }
            Assert.IsTrue(Task.WaitAll(new Task[] { io, close }, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(7, io.Result);
            Assert.IsTrue(connection.Disposed);
        }

        private sealed class FakeConnection : IDisposable
        {
            public int DisposeCount;
            public bool Disposed;
            public Action? OnDispose;

            public void Dispose()
            {
                Disposed = true;
                DisposeCount++;
                OnDispose?.Invoke();
            }
        }
    }
}
