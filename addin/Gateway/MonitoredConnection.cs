using System;

namespace Knx.EtsBridge.Addin
{
    // Keep the monitor transport alive until any borrowed synchronous I/O finishes.
    internal sealed class MonitoredConnection<T> where T : class, IDisposable
    {
        private readonly object _sync;
        private string? _owner;
        private T? _connection;

        public MonitoredConnection(object sync)
        {
            _sync = sync;
        }

        public void Attach(string owner, T connection)
        {
            lock (_sync)
            {
                if (_connection != null)
                    throw new InvalidOperationException("A group monitor connection is already active.");
                _owner = owner;
                _connection = connection;
            }
        }

        public TResult Use<TResult>(Func<T, TResult> action, Func<TResult> fallback)
        {
            lock (_sync)
            {
                return _connection != null ? action(_connection) : fallback();
            }
        }

        public void Close(string owner, Action<T> beforeDispose)
        {
            lock (_sync)
            {
                if (_connection == null || _owner != owner) return;
                var connection = _connection;
                _connection = null;
                _owner = null;
                try
                {
                    beforeDispose(connection);
                }
                finally
                {
                    connection.Dispose();
                }
            }
        }
    }
}
