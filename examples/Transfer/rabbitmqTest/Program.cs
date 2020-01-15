using RabbitMQ.Client;
using System;

namespace rabbitmqTest
{
    class Program
    {
        static void Main(string[] args)
        {

            Console.WriteLine("Hello World!");

            var _connectionFactory = new ConnectionFactory()
            {
                HostName = @"192.168.0.19",
                UserName = "rabbitmq",
                Password = "rabbitmq",
                VirtualHost = "/",
                AutomaticRecoveryEnabled = false,
                Port = 25672,
                SocketFactory = (s) => new RabbitMQ.Client.TcpClientAdapter(new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp))
            };
            _connectionFactory.RequestedHeartbeat = 60;
            _connectionFactory.RequestedConnectionTimeout = 1000 * 3;
            _connectionFactory.HandshakeContinuationTimeout = TimeSpan.FromSeconds(30);
            var _connection = _connectionFactory
                        .CreateConnection();
            var xx = _connection != null && _connection.IsOpen;
        }
    }
}
