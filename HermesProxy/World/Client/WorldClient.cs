﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HermesProxy.Enums;
using Framework.Constants;
using Framework.Cryptography;
using Framework;
using Framework.IO;
using Framework.Logging;
using HermesProxy.World.Enums;
using Framework.Networking;
using HermesProxy.World.Server;


namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        Socket _clientSocket;
        bool? _isSuccessful;
        string _username;
        Realm _realm;
        LegacyWorldCrypt _worldCrypt;
        Dictionary<Opcode, Action<WorldPacket>> _packetHandlers;
        GlobalSessionData _globalSession;
        System.Threading.Mutex _sendMutex = new System.Threading.Mutex();

        // packet order is not always the same as new client, sometimes we need to delay packet until another one
        Dictionary<Opcode, List<WorldPacket>> _delayedPacketsToServer;
        Dictionary<Opcode, List<ServerPacket>> _delayedPacketsToClient;

        public WorldClient()
        {
            InitializePacketHandlers();
        }

        public GlobalSessionData GetSession()
        {
            return _globalSession;
        }

        public GlobalSessionData Session => _globalSession;

        public bool ConnectToWorldServer(Realm realm, GlobalSessionData globalSession)
        {
            _worldCrypt = null;
            _realm = realm;
            _globalSession = globalSession;
            _username = globalSession.Username;
            _isSuccessful = null;
            _delayedPacketsToServer = new Dictionary<Opcode, List<WorldPacket>>();
            _delayedPacketsToClient = new Dictionary<Opcode, List<ServerPacket>>();

            Log.Print(LogType.Network, "Connecting to world server...");
            try
            {
                var ip = NetworkUtils.ResolveOrDirectIPv4(realm.ExternalAddress);
                Log.Print(LogType.Network, $"World Server address {realm.ExternalAddress}:{realm.Port} resolved as {ip}:{realm.Port}");
                _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                // Connect to the specified host.
                var endPoint = new IPEndPoint(ip, realm.Port);
                _clientSocket.BeginConnect(endPoint, ConnectCallback, null);
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Error, $"Socket Error: {ex.Message}");
                _isSuccessful = false;
            }

            while (_isSuccessful == null)
            { }

            return (bool)_isSuccessful;
        }

        public bool IsAuthenticated()
        {
            return _isSuccessful == true;
        }

        private void InitializeEncryption(byte[] sessionKey)
        {
            switch (Settings.ServerBuild)
            {
                case ClientVersionBuild.V1_12_1_5875:
                case ClientVersionBuild.V1_12_2_6005:
                case ClientVersionBuild.V1_12_3_6141:
                    _worldCrypt = new VanillaWorldCrypt();
                    break;
                case ClientVersionBuild.V2_4_3_8606:
                    _worldCrypt = new TbcWorldCrypt();
                    break;
            }

            if (_worldCrypt != null)
                _worldCrypt.Initialize(sessionKey);
        }

        public void Disconnect()
        {
            if (!IsConnected())
                return;

            _clientSocket.Shutdown(SocketShutdown.Both);
            _clientSocket.Disconnect(false);

            if (GetSession().WorldClient == this)
                GetSession().WorldClient = null;
        }

        public bool IsConnected()
        {
            return _clientSocket != null && _clientSocket.Connected;
        }

        private void ConnectCallback(IAsyncResult AR)
        {
            try
            {
                Log.Print(LogType.Network, "Connection established!");

                _clientSocket.EndConnect(AR);
                _clientSocket.ReceiveBufferSize = 65535;

                Task.Run(ReceiveLoop);
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Error, $"Connect Error: {ex.Message}");
                if (_isSuccessful == null)
                    _isSuccessful = false;
            }
        }

        private async Task<bool> ReceiveBufferFully(ArraySegment<byte> bufferToFill)
        {
            int alreadyReceived = 0;
            while (alreadyReceived < bufferToFill.Count)
            {
                var tmpArrayBuffer = new ArraySegment<byte>(bufferToFill.Array!, alreadyReceived + bufferToFill.Offset, bufferToFill.Count - alreadyReceived);
                int receive = await _clientSocket.ReceiveAsync(tmpArrayBuffer, SocketFlags.None);
                if (receive == 0)
                    return false;
                alreadyReceived += receive;
            }

            return true;
        }

        private async Task ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    byte[] headerBuffer = new byte[LegacyServerPacketHeader.StructSize];
                    if (!await ReceiveBufferFully(headerBuffer))
                    {
                        Log.PrintNet(LogType.Error, LogNetDir.S2P, "Socket Closed By GameWorldServer (header)");
                        if (_isSuccessful == null)
                            _isSuccessful = false;
                        else if (GetSession().WorldClient == this)
                            GetSession().OnDisconnect();
                        return;
                    }

                    if (_worldCrypt != null)
                        _worldCrypt.Decrypt(headerBuffer, LegacyServerPacketHeader.StructSize);

                    LegacyServerPacketHeader header = new LegacyServerPacketHeader();
                    header.Read(headerBuffer);
                    ushort packetSize = header.Size;

                    if (packetSize != 0)
                    {
                        byte[] buffer = new byte[packetSize];

                        // copy the opcode into the new buffer
                        buffer[0] = headerBuffer[2];
                        buffer[1] = headerBuffer[3];

                        if (!await ReceiveBufferFully(new ArraySegment<byte>(buffer, 2, buffer.Length - 2)))
                        {
                            Log.PrintNet(LogType.Error, LogNetDir.S2P, "Socket Closed By GameWorldServer (payload)");
                            if (_isSuccessful == null)
                                _isSuccessful = false;
                            else if (GetSession().WorldClient == this)
                                GetSession().OnDisconnect();
                            return;
                        }

                        WorldPacket packet = new WorldPacket(buffer);
                        packet.SetReceiveTime(Environment.TickCount);
                        HandlePacket(packet);
                    }
                }
            }
            catch(Exception e)
            {
                Log.PrintNet(LogType.Error, LogNetDir.S2P, $"Packet Read Error: {e.Message}{Environment.NewLine}{e.StackTrace}");
                if (_isSuccessful == null)
                    _isSuccessful = false;
                else
                {
                    Disconnect();
                    GetSession().OnDisconnect();
                }
            }
        }

        // C P>S: Sends data to world server
        private void SendPacket(WorldPacket packet)
        {
            _sendMutex.WaitOne();
            try
            {
                ByteBuffer buffer = new ByteBuffer();
                LegacyClientPacketHeader header = new LegacyClientPacketHeader();

                header.Size = (ushort)(packet.GetSize() + sizeof(uint)); // size includes the opcode
                header.Opcode = packet.GetOpcode();
                header.Write(buffer);

                Log.PrintNet(LogType.Debug, LogNetDir.P2S, $"Sending opcode {LegacyVersion.GetUniversalOpcode(header.Opcode)} ({header.Opcode}) with size {header.Size}.");

                byte[] headerArray = buffer.GetData();
                if (_worldCrypt != null)
                    _worldCrypt.Encrypt(headerArray, LegacyClientPacketHeader.StructSize);
                buffer.Clear();
                buffer.WriteBytes(headerArray);

                buffer.WriteBytes(packet.GetData(), packet.GetSize());

                _clientSocket.Send(buffer.GetData(), SocketFlags.None);
            }
            catch (Exception ex)
            {
                Log.PrintNet(LogType.Error, LogNetDir.P2S, $"Packet Write Error: {ex.Message}");
                if (_isSuccessful == null)
                    _isSuccessful = false;
            }
            _sendMutex.ReleaseMutex();
        }

        public void SendPacketToClient(ServerPacket packet, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
        {
            Opcode opcode = packet.GetUniversalOpcode();
            if (delayUntilOpcode != Opcode.MSG_NULL_ACTION)
            {
                if (_delayedPacketsToClient.ContainsKey(delayUntilOpcode))
                    _delayedPacketsToClient[delayUntilOpcode].Add(packet);
                else
                {
                    List<ServerPacket> packets = new List<ServerPacket>();
                    packets.Add(packet);
                    _delayedPacketsToClient.Add(delayUntilOpcode, packets);
                }
                return;
            }

            SendPacketToClientDirect(packet);
            SendDelayedPacketsToClientOnOpcode(opcode);
        }

        private void SendPacketToClientDirect(ServerPacket packet)
        {
            var pendingPackets = GetSession().GameState.PendingUninstancedPackets;
            if (packet.GetConnection() == ConnectionType.Realm)
            {
                GetSession().RealmSocket.SendPacket(packet);
            }
            else
            {
                if (GetSession().InstanceSocket == null &&
                   !GetSession().GameState.IsConnectedToInstance)
                {
                    lock (pendingPackets)
                    {
                        if (GetSession().InstanceSocket == null &&
                            !GetSession().GameState.IsConnectedToInstance)
                        {
                            pendingPackets.Enqueue(packet);
                            Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"Can't send opcode {packet.GetUniversalOpcode()} ({packet.GetOpcode()}) before entering world! Queue");
                            return;
                        }
                    }
                }

                // block these packets until connected to instance
                while (GetSession().InstanceSocket == null)
                {
                    Log.PrintNet(LogType.Network, LogNetDir.P2C, $"Waiting to send {packet.GetUniversalOpcode()} ({packet.GetOpcode()}).");
                    System.Threading.Thread.Sleep(200);
                }

                var socket = GetSession().InstanceSocket;
                if (pendingPackets.Count > 0)
                {
                    lock (pendingPackets)
                    {
                        while (pendingPackets.TryDequeue(out var oldPacket))
                        {
                            socket.SendPacket(oldPacket);
                        }
                    }
                }

                socket.SendPacket(packet);
            }
        }

        public void SendPacketToServer(WorldPacket packet, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
        {
            Opcode opcode = packet.GetUniversalOpcode(false);
            if (delayUntilOpcode != Opcode.MSG_NULL_ACTION)
            {
                if (_delayedPacketsToServer.ContainsKey(delayUntilOpcode))
                    _delayedPacketsToServer[delayUntilOpcode].Add(packet);
                else
                {
                    List<WorldPacket> packets = new List<WorldPacket>();
                    packets.Add(packet);
                    _delayedPacketsToServer.Add(delayUntilOpcode, packets);
                }
                return;
            }

            SendPacket(packet);
            SendDelayedPacketsToServerOnOpcode(opcode);
        }

        private void SendDelayedPacketsToServerOnOpcode(Opcode opcode)
        {
            if (_delayedPacketsToServer.ContainsKey(opcode))
            {
                List<WorldPacket> packets = _delayedPacketsToServer[opcode];
                for (int i = packets.Count - 1; i >= 0; i--)
                {
                    SendPacket(packets[i]);
                    packets.RemoveAt(i);
                }
            }
        }

        private void SendDelayedPacketsToClientOnOpcode(Opcode opcode)
        {
            if (_delayedPacketsToClient.ContainsKey(opcode))
            {
                List<ServerPacket> packets = _delayedPacketsToClient[opcode];
                for (int i = packets.Count - 1; i >= 0; i--)
                {
                    SendPacketToClientDirect(packets[i]);
                    packets.RemoveAt(i);
                }
            }
        }

        private void HandlePacket(WorldPacket packet)
        {
            Opcode universalOpcode = packet.GetUniversalOpcode(false);
            Log.PrintNet(LogType.Debug, LogNetDir.S2P, $"Received opcode {universalOpcode} ({packet.GetOpcode()}).");

            switch (universalOpcode)
            {
                case Opcode.SMSG_AUTH_CHALLENGE:
                    HandleAuthChallenge(packet);
                    break;
                case Opcode.SMSG_AUTH_RESPONSE:
                    HandleAuthResponse(packet);
                    break;
                case Opcode.SMSG_ADDON_INFO:
                    break; // don't need to handle
                default:
                    if (_packetHandlers.ContainsKey(universalOpcode))
                    {
                        _packetHandlers[universalOpcode](packet);
                    }
                    else
                    {
                        Log.PrintNet(LogType.Warn, LogNetDir.S2P, $"No handler for opcode {universalOpcode} ({packet.GetOpcode()}) (Got unknown packet from WorldServer)");
                        if (_isSuccessful == null)
                            _isSuccessful = false;
                    }
                    break;
            }

            SendDelayedPacketsToServerOnOpcode(universalOpcode);
        }

        private void HandleAuthChallenge(WorldPacket packet)
        {
            if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
            {
                uint one = packet.ReadUInt32();
            }

            uint seed = packet.ReadUInt32();

            if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
            {
                BigInteger seed1 = packet.ReadBytes(16).ToBigInteger();
                BigInteger seed2 = packet.ReadBytes(16).ToBigInteger();
            }

            var rand = System.Security.Cryptography.RandomNumberGenerator.Create();
            byte[] bytes = new byte[4];
            rand.GetBytes(bytes);
            BigInteger ourSeed = bytes.ToBigInteger();

            SendAuthResponse((uint)ourSeed, seed);
        }

        public void SendAuthResponse(uint clientSeed, uint serverSeed)
        {
            uint zero = 0;

            byte[] authResponse = Framework.Cryptography.HashAlgorithm.SHA1.Hash
            (
                Encoding.ASCII.GetBytes(_username.ToUpper()),
                BitConverter.GetBytes(zero),
                BitConverter.GetBytes(clientSeed),
                BitConverter.GetBytes(serverSeed),
                GetSession().AuthClient.GetSessionKey()
            );

            WorldPacket packet = new WorldPacket(Opcode.CMSG_AUTH_SESSION);
            packet.WriteUInt32((uint)Settings.ServerBuild);
            packet.WriteUInt32(_realm.Id.Index);
            packet.WriteBytes(_username.ToUpper().ToCString());

            if (Settings.ServerBuild >= ClientVersionBuild.V3_0_2_9056)
                packet.WriteUInt32(zero); // LoginServerType

            packet.WriteUInt32(clientSeed);

            if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
            {
                packet.WriteUInt32(_realm.Id.Region);
                packet.WriteUInt32(_realm.Id.Site);
                packet.WriteUInt32(_realm.Id.Index);
            }

            if (Settings.ServerBuild >= ClientVersionBuild.V3_2_0_10192)
                packet.WriteUInt64(zero); // DosResponse

            packet.WriteBytes(authResponse);

            // packet.WriteUInt32(zero); // length of addon data
            byte[] addonBytes = new byte[] { 208, 1, 0, 0, 120, 156, 117, 207, 61, 14, 194, 48, 12, 5, 224, 114, 14, 184, 12, 97, 64, 149, 154, 133, 150, 25, 153, 196, 173, 172, 38, 78, 21, 82, 126, 58, 113, 66, 206, 68, 81, 133, 24, 98, 188, 126, 126, 79, 182, 114, 52, 77, 16, 237, 105, 59, 154, 68, 129, 143, 101, 177, 242, 183, 77, 85, 204, 163, 190, 166, 32, 37, 135, 45, 161, 179, 154, 152, 60, 12, 210, 18, 177, 37, 238, 230, 130, 87, 102, 187, 224, 207, 144, 170, 208, 9, 185, 197, 26, 188, 39, 9, 35, 180, 73, 188, 105, 175, 235, 49, 94, 241, 33, 227, 72, 206, 42, 224, 94, 212, 146, 47, 3, 154, 79, 237, 58, 183, 132, 190, 14, 166, 199, 180, 252, 146, 167, 53, 152, 24, 102, 121, 102, 114, 0, 178, 51, 196, 12, 26, 112, 200, 242, 27, 77, 4, 139, 117, 79, 206, 253, 99, 98, 140, 178, 145, 71, 13, 12, 29, 198, 159, 190, 1, 43, 0, 141, 195 };
            packet.WriteBytes(addonBytes);

            SendPacket(packet);

            InitializeEncryption(GetSession().AuthClient.GetSessionKey());
        }

        private void HandleAuthResponse(WorldPacket packet)
        {
            AuthResult result = (AuthResult)packet.ReadUInt8();

            uint billingTimeRemaining = packet.ReadUInt32();
            byte billingFlags = packet.ReadUInt8();
            uint billingTimeRested = packet.ReadUInt32();

            if (Settings.ServerBuild >= ClientVersionBuild.V2_0_1_6180)
            {
                byte expansion = packet.ReadUInt8();
            }

            // uncomment to test encryption
            //WorldPacket charEnum = new WorldPacket(Opcode.CMSG_ENUM_CHARACTERS);
            //SendPacket(charEnum);

            if (result == AuthResult.AUTH_OK)
            {
                Log.Print(LogType.Network, "Authentication succeeded!");
                _isSuccessful = true;
            }
            else
            {
                Log.Print(LogType.Network, "Authentication failed!");
                _isSuccessful = false;
            }
        }

        public void SendPing(uint ping, uint latency)
        {
            if (!IsConnected() || _isSuccessful == false)
                return;

            WorldPacket packet = new WorldPacket(Opcode.CMSG_PING);
            Random rd = new Random();
            uint pingValue = (uint)rd.Next(320,350);
            Log.Print(LogType.Warn, $"Ping value{ping}  {latency}  {pingValue}     ");
            packet.WriteUInt32(pingValue);
            packet.WriteUInt32(pingValue);
            SendPacket(packet);
        }

        public void InitializePacketHandlers()
        {
            _packetHandlers = new();

            foreach (var methodInfo in typeof(WorldClient).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                foreach (var msgAttr in methodInfo.GetCustomAttributes<PacketHandlerAttribute>())
                {
                    if (msgAttr == null)
                        continue;

                    if (msgAttr.Opcode == Opcode.MSG_NULL_ACTION)
                        continue;

                    if (_packetHandlers.ContainsKey(msgAttr.Opcode))
                    {
                        Log.Print(LogType.Error, $"Tried to override OpcodeHandler of {_packetHandlers[msgAttr.Opcode]} with {methodInfo.Name} (Opcode {msgAttr.Opcode})");
                        continue;
                    }

                    var parameters = methodInfo.GetParameters();
                    if (parameters.Length == 0)
                    {
                        Log.Print(LogType.Error, $"Method: {methodInfo.Name} Has no parameters");
                        continue;
                    }

                    if (parameters[0].ParameterType != typeof(WorldPacket))
                    {
                        Log.Print(LogType.Error, $"Method: {methodInfo.Name} has wrong BaseType");
                        continue;
                    }

                    var del = (Action<WorldPacket>)Delegate.CreateDelegate(typeof(Action<WorldPacket>), this, methodInfo);

                    _packetHandlers[msgAttr.Opcode] = del;
                }
            }
        }



	private Sha1 _hashOut;

	private Sha1 _hashIn;

	private byte[] _wmh;

    private bool _allAddonsAllowed;

        private void HandleGenericVersion(ByteBuffer packet)
	{
		byte[] wmh = packet.ReadBytes(16u);
		_wmh = wmh;
		packet.ReadBytes(16u);
		packet.ReadUInt32();
		ByteBuffer byteBuffer = new ByteBuffer();
		byteBuffer.WriteUInt8(1);
		SendPacketToServer(byteBuffer);
	}
 
 
 
	private void SendPacketToServer(ByteBuffer payload)
	{
		byte[] data = payload.GetData();
		_hashIn.ProcessBuffer(data, data.Length);
		WorldPacket worldPacket = new WorldPacket(743u);
		worldPacket.WriteBytes(data);
		SendPacketToServer(worldPacket);
	}

    	private void Handle(ByteBuffer payload)
	{
        byte data = payload.ReadUInt8();
		switch (data)
		{
		case 0:
			HandleGenericVersion(payload);
			break;
        case 2:
			HandleTbcVersion(payload);
		    break;
        case 5:
			HandleFutureVersion(payload);
			break;
		}
	}

    private void HandleTbcVersion(ByteBuffer packet)
	{
		byte count = packet.ReadUInt8();
		byte[] array = packet.ReadBytes(count);
		if (_wmh != null && _allAddonsAllowed)
		{
			byte[] array2 = new byte[4] { 206, 250, 237, 254 };
			byte[] data = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array, array2);
			byte[] data2 = MD5.Create().ComputeHash(array);
			ByteBuffer byteBuffer = new ByteBuffer();
			byteBuffer.WriteUInt8(2);
			byteBuffer.WriteBytes(data);
			byteBuffer.WriteBytes(data2);
			SendPacketToServer(byteBuffer);
		}
	}

    	private void HandleFutureVersion(ByteBuffer packet)
	{
		CSV.BinaryFix binaryFix = CSV.KnownFixes.FirstOrDefault((CSV.BinaryFix x) => x.Expected.SequenceEqual(_wmh));
		if (binaryFix.Fixes != null)
		{
			byte[] fixValue = packet.ReadBytes(16u);
			CSV.BinaryFix.Fix fix = binaryFix.Fixes.FirstOrDefault((CSV.BinaryFix.Fix x) => x.Actual.SequenceEqual(fixValue));
			if (fix.Buffer != null)
			{
				ByteBuffer byteBuffer = new ByteBuffer();
				byteBuffer.WriteUInt8(4);
				byteBuffer.WriteBytes(fix.Accept);
				SendPacketToServer(byteBuffer);
				_hashIn = new Sha1();
				_hashIn.SetBase(fix.Buffer);
				_hashOut = new Sha1();
				_hashOut.SetBase(fix.Checksum);
				_allAddonsAllowed = true;
			}
		}
	}


	[PacketHandler(3184u)]
	private void HandleLegacyAddonVerification(WorldPacket packet)
	{
		if (_hashIn == null)
		{
			Sha1Helper sha1Helper = new Sha1Helper(GetSession().AuthClient.GetSessionKey());
			_hashIn = new Sha1();
			byte[] hash = sha1Helper.GetHash();
			_hashIn.SetBase(hash);
			_hashOut = new Sha1();
			byte[] hash2 = sha1Helper.GetHash();
			_hashOut.SetBase(hash2);
		}
		byte[] array = packet.ReadToEnd();
		_hashOut.ProcessBuffer(array, array.Length);
		Handle(new ByteBuffer(array));
	}


    	private struct Sha1Helper
	{
		private readonly byte[] _part1;

		private byte[] _part2;

		private readonly byte[] _part3;

		private int _alreadyTaken;

		public Sha1Helper(byte[] sk)
		{
			_alreadyTaken = 0;
			byte[] array = sk.Take(20).ToArray();
			_part1 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array);
			_part2 = new byte[20];
			byte[] array2 = sk.Skip(20).ToArray();
			_part3 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array2);
			CalcHash();
		}

		public byte[] GetHash()
		{
			byte[] array = new byte[16];
			for (int i = 0; i < 16; i++)
			{
				array[i] = GetOneByte();
			}
			return array;
		}

		private byte GetOneByte()
		{
			if (_alreadyTaken >= _part2.Length)
			{
				CalcHash();
			}
			byte result = _part2[_alreadyTaken];
			_alreadyTaken++;
			return result;
		}

		private void CalcHash()
		{
			byte[] part = Framework.Cryptography.HashAlgorithm.SHA1.Hash(_part1, _part2, _part3);
			_part2 = part;
			_alreadyTaken = 0;
		}
	}
    }
}
