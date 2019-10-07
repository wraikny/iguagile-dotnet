using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Iguagile
{
    public enum MessageType : byte
    {
        NewConnection,
        ExitConnection,
        Instantiate,
        Destroy,
        RequestObjectControlAuthority,
        TransferObjectControlAuthority,
        MigrateHost,
        Register,
        Transform,
        Rpc,
    }

    public enum RpcTargets : byte
    {
        AllClients,
        OtherClients,
        AllClientsBuffered,
        OtherClientsBuffered,
        Host,
        Server,
    }

    public class IguagileClient : IDisposable
    {
        private IClient client;

        private readonly Dictionary<int, User> users = new Dictionary<int, User>();
        private readonly Dictionary<string, object> rpcMethods = new Dictionary<string, object>();

        public int UserId { get; private set; }
        public bool IsHost { get; private set; }

        public bool IsConnected => client?.IsConnected ?? false;
        
        public event Action OnConnected = delegate { };
        public event Action OnClosed = delegate { };
        public event Action<Exception> OnError = delegate { };

        public async Task StartAsync(string address, int port, Protocol protocol)
        {
            switch (protocol)
            {
                case Protocol.Tcp:
                    client = new TcpClient();
                    break;
                default:
                    throw new ArgumentException("invalid protocol");
            }

            client.OnConnected += OnConnected;
            client.OnClosed += OnClosed;
            client.OnError += OnError;
            client.OnReceived += ClientReceived;
            await client.StartAsync(address, port);
        }

        public void Disconnect()
        {
            if (client != null)
            {
                client.Disconnect();
                client = null;
            }
        }

        public void AddRpc(string methodName, object receiver)
        {
            lock(rpcMethods)
            {
                rpcMethods[methodName] = receiver;
            }
        }

        public void RemoveRpc(object receiver)
        {
            lock(rpcMethods)
            {
                foreach (var method in rpcMethods
                    .Where(x => ReferenceEquals(x.Value, receiver))
                    .Select(x => x.Key)
                    .ToArray()
                )
                {
                    rpcMethods.Remove(method);
                }
            }
        }

        public async Task Rpc(string methodName, RpcTargets target, params object[] args)
        {
            var objects = (new object[] { methodName }).Concat(args).ToArray();
            var data = Serialize(target, MessageType.Rpc, objects);
            await SendAsync(data);
        }

        public void Dispose()
        {
            Disconnect();
        }

        private byte[] Serialize(RpcTargets target, MessageType messageType, params object[] message)
        {
            var serialized = MessagePackSerializer.Serialize(message);
            var data = new byte[] { (byte)target, (byte)messageType };
            return data.Concat(serialized).ToArray();
        }

        private async Task SendAsync(byte[] data)
        {
            if (data.Length >= (1 << 16) - 16)
            {
                throw new Exception("too long data");
            }

            if (data.Length != 0)
            {
                await client.SendAsync(data);
            }
        }

        private const int HeaderSize = 3;

        private void ClientReceived(byte[] message)
        {
            var id = BitConverter.ToInt16(message, 0);
            var messageType = (MessageType)message[2];
            switch (messageType)
            {
                case MessageType.Rpc:
                    InvokeRpc(message.Skip(HeaderSize).ToArray());
                    break;
                case MessageType.NewConnection:
                    AddUser(id);
                    break;
                case MessageType.ExitConnection:
                    RemoveUser(id);
                    break;
                case MessageType.MigrateHost:
                    MigrateHost();
                    break;
                case MessageType.Register:
                    Register(id);
                    break;
            }
        }

        private void InvokeRpc(byte[] data)
        {
            var objects = MessagePackSerializer.Deserialize<object[]>(data);
            var methodName = (string)objects[0];
            var args = objects.Skip(1).ToArray();

            object behaviour;
            lock (rpcMethods)
            {
                if (!rpcMethods.ContainsKey(methodName))
                {
                    return;
                }

                behaviour = rpcMethods[methodName];
            }

            var type = behaviour.GetType();
            var flag = BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var method = type.GetMethod(methodName, flag);
            method?.Invoke(behaviour, args);
        }

        private void AddUser(int id)
        {
            users[id] = new User(id);
        }

        private void RemoveUser(int id)
        {
            users.Remove(id);
        }

        private void MigrateHost()
        {
            IsHost = true;
        }

        private void Register(int id)
        {
            UserId = id;
        }
    }
}
