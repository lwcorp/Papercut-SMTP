namespace Papercut.Core.Network
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Linq;
    using System.Net.Sockets;
    using System.Reactive.Concurrency;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Threading;

    using Serilog;

    public class ConnectionManager : IDisposable
    {
        readonly Func<int, Socket, IProtocol, IConnection> _connectionFactory;

        readonly ConcurrentDictionary<int, IConnection> _connections =
            new ConcurrentDictionary<int, IConnection>();

        readonly CompositeDisposable _disposables = new CompositeDisposable();

        int _connectionID;

        bool _isInitialized;

        public ConnectionManager(
            Func<int, Socket, IProtocol, IConnection> connectionFactory,
            ILogger logger)
        {
            Logger = logger;
            _connectionFactory = connectionFactory;
        }

        public ILogger Logger { get; set; }

        public void Dispose()
        {
            _disposables.Dispose();
        }

        public IConnection CreateConnection(Socket clientSocket, IProtocol protocol)
        {
            Interlocked.Increment(ref _connectionID);
            IConnection connection = _connectionFactory(_connectionID, clientSocket, protocol);
            connection.ConnectionClosed += ConnectionClosed;
            _connections.TryAdd(connection.Id, connection);

            InitCleanupObservables();

            return connection;
        }

        void InitCleanupObservables()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            Logger.Debug("Initializing Background Processes...");

            _disposables.Add(
                Observable.Timer(
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(5),
                    TaskPoolScheduler.Default).Subscribe(
                        t =>
                        {
                            // Get the number of current connections
                            int[] keys = _connections.Keys.ToArray();

                            // Loop through the connections
                            foreach (int key in keys)
                            {
                                // If they have been idle for too long, disconnect them
                                if (DateTime.Now < _connections[key].LastActivity.AddMinutes(20))
                                {
                                    Logger.Information(
                                        "Session timeout, disconnecting {ConnectionId}",
                                        _connections[key].Id);
                                    _connections[key].Close();
                                }
                            }
                        }));

            // print out status every 20 minutes
            _disposables.Add(
                Observable.Timer(
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(20),
                    TaskPoolScheduler.Default).Subscribe(
                        t =>
                        {
                            double memusage = (double)Process.GetCurrentProcess().WorkingSet64
                                              / 1024 / 1024;
                            Logger.Debug(
                                "Status: {ConnectionCount} Connections {MemoryUsed} Memory Used",
                                _connections.Count,
                                memusage.ToString("0.#") + "MB");
                        }));
        }

        public void CloseAll()
        {
            // Close all open connections
            foreach (IConnection connection in
                _connections.Values.Where(connection => connection != null))
            {
                connection.Close(false);
            }
        }

        void ConnectionClosed(object sender, EventArgs e)
        {
            var connection = sender as IConnection;
            if (connection == null) return;

            IConnection noneed;
            _connections.TryRemove(connection.Id, out noneed);
        }
    }
}