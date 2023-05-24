using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Framework;
using Framework.Constants;
using Framework.Cryptography;
using Framework.IO;
using Framework.Logging;
using Framework.Networking;
using HermesProxy.Enums;

namespace HermesProxy.Auth;

public class AuthClient
{
	private static readonly Action<ByteBuffer> _debugTraceBreakpointHandler;

	private GlobalSessionData _globalSession;

	private Socket _clientSocket;

	private TaskCompletionSource<AuthResult> _response;

	private TaskCompletionSource _hasRealmlist;

	private bool _realmlistRequestIsPending;

	private byte[] _passwordHash;

	private BigInteger _key;

	private byte[] _m2;

	private string _username;

	private string _locale;

	public AuthClient(GlobalSessionData globalSession)
	{
		_globalSession = globalSession;
	}

	public GlobalSessionData GetSession()
	{
		return _globalSession;
	}

	public AuthResult ConnectToAuthServer(string username, string password, string locale)
	{
		_username = username;
		_locale = locale;
		_response = new TaskCompletionSource<AuthResult>();
		_hasRealmlist = new TaskCompletionSource();
		_realmlistRequestIsPending = false;
		string text = _username + ":" + password;
		_passwordHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(Encoding.ASCII.GetBytes(text.ToUpper()));
		try
		{
			IPAddress iPAddress = NetworkUtils.ResolveOrDirectIPv4(Settings.ServerAddress);
			Log.PrintNet(LogType.Network, LogNetDir.P2S, $"Connecting to auth server... (realmlist addr: {Settings.ServerAddress}:{Settings.ServerPort}) (resolved as: {iPAddress}:{Settings.ServerPort})", "ConnectToAuthServer", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			_clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			IPEndPoint iPEndPoint = new IPEndPoint(iPAddress, Settings.ServerPort);
			_clientSocket.BeginConnect(iPEndPoint, ConnectCallback, null);
		}
		catch (Exception ex)
		{
			Log.PrintNet(LogType.Error, LogNetDir.P2S, "Socket Error: " + ex.Message, "ConnectToAuthServer", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			_response.SetResult(AuthResult.FAIL_INTERNAL_ERROR);
		}
		_response.Task.Wait();
		return _response.Task.Result;
	}

	public AuthResult Reconnect()
	{
		_response = new TaskCompletionSource<AuthResult>();
		_hasRealmlist = new TaskCompletionSource();
		_realmlistRequestIsPending = false;
		try
		{
			IPAddress iPAddress = NetworkUtils.ResolveOrDirectIPv4(Settings.ServerAddress);
			Log.PrintNet(LogType.Network, LogNetDir.P2S, $"Reconnecting to auth server... (realmlist addr: {Settings.ServerAddress}:{Settings.ServerPort}) (resolved as: {iPAddress}:{Settings.ServerPort})", "Reconnect", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			_clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			IPEndPoint iPEndPoint = new IPEndPoint(iPAddress, Settings.ServerPort);
			_clientSocket.BeginConnect(iPEndPoint, ConnectCallback, null);
		}
		catch (Exception ex)
		{
			Log.PrintNet(LogType.Error, LogNetDir.P2S, "Socket Error: " + ex.Message, "Reconnect", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			_response.SetResult(AuthResult.FAIL_INTERNAL_ERROR);
		}
		_response.Task.Wait();
		return _response.Task.Result;
	}

	private void SetAuthResponse(AuthResult response)
	{
		_response.TrySetResult(response);
	}

	public void Disconnect()
	{
		if (IsConnected())
		{
			_clientSocket.Shutdown(SocketShutdown.Both);
			_clientSocket.Disconnect(false);
		}
	}

	public bool IsConnected()
	{
		if (_clientSocket != null)
		{
			return _clientSocket.Connected;
		}
		return false;
	}

	public byte[] GetSessionKey()
	{
		return _key.ToCleanByteArray();
	}

	private void ConnectCallback(IAsyncResult AR)
	{
		try
		{
			_clientSocket.EndConnect(AR);
			_clientSocket.ReceiveBufferSize = 65535;
			byte[] array = new byte[_clientSocket.ReceiveBufferSize];
			_clientSocket.BeginReceive(array, 0, array.Length, SocketFlags.None, ReceiveCallback, array);
			SendLogonChallenge(reconnect: false);
		}
		catch (Exception ex)
		{
			Log.Print(LogType.Error, "Connect Error: " + ex.Message, "ConnectCallback", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
		}
	}

	private void ReconnectCallback(IAsyncResult AR)
	{
		try
		{
			_clientSocket.EndConnect(AR);
			_clientSocket.ReceiveBufferSize = 65535;
			byte[] array = new byte[_clientSocket.ReceiveBufferSize];
			_clientSocket.BeginReceive(array, 0, array.Length, SocketFlags.None, ReceiveCallback, array);
			SendLogonChallenge(reconnect: true);
		}
		catch (Exception ex)
		{
			Log.PrintNet(LogType.Error, LogNetDir.P2S, "Connect Error: " + ex.Message, "ReconnectCallback", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
		}
	}

	private void ReceiveCallback(IAsyncResult AR)
	{
		try
		{
			int num = _clientSocket.EndReceive(AR);
			if (num == 0)
			{
				SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
				Log.PrintNet(LogType.Error, LogNetDir.S2P, "Socket Closed By Server", "ReceiveCallback", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
				return;
			}
			byte[] buffer = (byte[])AR.AsyncState;
			HandlePacket(buffer, num);
			byte[] array = new byte[_clientSocket.ReceiveBufferSize];
			_clientSocket.BeginReceive(array, 0, array.Length, SocketFlags.None, ReceiveCallback, array);
		}
		catch (Exception ex)
		{
			Log.Print(LogType.Error, "Packet Read Error: " + ex.Message, "ReceiveCallback", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
		}
	}

	private void SendCallback(IAsyncResult AR)
	{
		try
		{
			_clientSocket.EndSend(AR);
		}
		catch (Exception ex)
		{
			Log.PrintNet(LogType.Error, LogNetDir.P2S, "Packet Send Error: " + ex.Message, "SendCallback", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
		}
	}

	private void SendPacket(ByteBuffer packet)
	{
		try
		{
			_clientSocket.BeginSend(packet.GetData(), 0, (int)packet.GetSize(), SocketFlags.None, SendCallback, null);
		}
		catch (Exception ex)
		{
			Log.PrintNet(LogType.Error, LogNetDir.P2S, "Packet Write Error: " + ex.Message, "SendPacket", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
		}
	}

	private void HandlePacket(byte[] buffer, int size)
	{
		ByteBuffer byteBuffer = new ByteBuffer(buffer);
		AuthCommand authCommand = (AuthCommand)byteBuffer.ReadUInt8();
		Log.PrintNet(LogType.Debug, LogNetDir.S2P, $"Received opcode {authCommand} size {size}.", "HandlePacket", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
		switch (authCommand)
		{
		case AuthCommand.LOGON_CHALLENGE:
			HandleLogonChallenge(byteBuffer);
			return;
		case AuthCommand.LOGON_PROOF:
			HandleLogonProof(byteBuffer);
			return;
		case AuthCommand.RECONNECT_CHALLENGE:
			HandleReconnectChallenge(byteBuffer);
			return;
		case AuthCommand.RECONNECT_PROOF:
			HandleReconnectProof(byteBuffer);
			return;
		case AuthCommand.REALM_LIST:
			HandleRealmList(byteBuffer);
			return;
		}
		Log.PrintNet(LogType.Error, LogNetDir.S2P, $"No handler for opcode {authCommand}!", "HandlePacket", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
		SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
	}

	private void SendLogonChallenge(bool reconnect)
	{
		ByteBuffer byteBuffer = new ByteBuffer();
		byteBuffer.WriteUInt8((byte)(reconnect ? 2 : 0));
		byteBuffer.WriteUInt8((byte)((LegacyVersion.ExpansionVersion > 1) ? 8u : 3u));
		byteBuffer.WriteUInt16((ushort)(_username.Length + 30));
		byteBuffer.WriteBytes(Encoding.ASCII.GetBytes("WoW"));
		byteBuffer.WriteUInt8(0);
		byteBuffer.WriteUInt8(LegacyVersion.ExpansionVersion);
		byteBuffer.WriteUInt8(LegacyVersion.MajorVersion);
		byteBuffer.WriteUInt8(LegacyVersion.MinorVersion);
		byteBuffer.WriteUInt16((ushort)Settings.ServerBuild);
		byteBuffer.WriteBytes(Encoding.ASCII.GetBytes(Settings.ReportedPlatform.Reverse()));
		byteBuffer.WriteUInt8(0);
		byteBuffer.WriteBytes(Encoding.ASCII.GetBytes(Settings.ReportedOS.Reverse()));
		byteBuffer.WriteUInt8(0);
		byteBuffer.WriteBytes(Encoding.ASCII.GetBytes(_locale.Reverse()));
		byteBuffer.WriteUInt32(60u);
		byteBuffer.WriteUInt32(16777343u);
		byteBuffer.WriteUInt8((byte)_username.Length);
		byteBuffer.WriteBytes(Encoding.ASCII.GetBytes(_username));
		SendPacket(byteBuffer);
	}

	private void HandleLogonChallenge(ByteBuffer packet)
	{
		packet.ReadUInt8();
		AuthResult authResult = (AuthResult)packet.ReadUInt8();
		if (authResult != 0)
		{
			Log.Print(LogType.Error, $"Login failed. Reason: {authResult}", "HandleLogonChallenge", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(authResult);
			return;
		}
		byte[] array = packet.ReadBytes(32u);
		packet.ReadUInt8();
		byte[] array2 = packet.ReadBytes(1u);
		packet.ReadUInt8();
		byte[] array3 = packet.ReadBytes(32u);
		byte[] array4 = packet.ReadBytes(32u);
		byte[] array5 = packet.ReadBytes(16u);
		packet.ReadUInt8();
		BigInteger bigInteger = new BigInteger(3);
		BigInteger bigInteger2 = array.ToBigInteger();
		BigInteger bigInteger3 = array2.ToBigInteger();
		BigInteger bigInteger4 = array3.ToBigInteger();
		array4.ToBigInteger();
		array5.ToBigInteger();
		BigInteger bigInteger5 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array4, _passwordHash).ToBigInteger();
		RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create();
		BigInteger bigInteger6;
		BigInteger bigInteger7;
		do
		{
			byte[] array6 = new byte[19];
			randomNumberGenerator.GetBytes(array6);
			bigInteger6 = array6.ToBigInteger();
			bigInteger7 = bigInteger3.ModPow(bigInteger6, bigInteger4);
		}
		while (bigInteger7.ModPow(1, bigInteger4) == 0L);
		BigInteger bigInteger8 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(bigInteger7.ToCleanByteArray(), bigInteger2.ToCleanByteArray()).ToBigInteger();
		byte[] array7 = ((bigInteger2 + bigInteger * (bigInteger4 - bigInteger3.ModPow(bigInteger5, bigInteger4))) % bigInteger4).ModPow(bigInteger6 + bigInteger8 * bigInteger5, bigInteger4).ToCleanByteArray();
		if (array7.Length < 32)
		{
			byte[] array8 = new byte[32];
			Buffer.BlockCopy(array7, 0, array8, 32 - array7.Length, array7.Length);
			array7 = array8;
		}
		byte[] array9 = new byte[40];
		byte[] array10 = new byte[16];
		for (int i = 0; i < 16; i++)
		{
			array10[i] = array7[i * 2];
		}
		byte[] array11 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array10);
		for (int j = 0; j < 20; j++)
		{
			array9[j * 2] = array11[j];
		}
		for (int k = 0; k < 16; k++)
		{
			array10[k] = array7[k * 2 + 1];
		}
		array11 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array10);
		for (int l = 0; l < 20; l++)
		{
			array9[l * 2 + 1] = array11[l];
		}
		_key = array9.ToBigInteger();
		byte[] array12 = new byte[20];
		byte[] array13 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(bigInteger4.ToCleanByteArray());
		for (int m = 0; m < 20; m++)
		{
			array12[m] = array13[m];
		}
		byte[] array14 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(bigInteger3.ToCleanByteArray());
		for (int n = 0; n < 20; n++)
		{
			array12[n] ^= array14[n];
		}
		byte[] array15 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(Encoding.ASCII.GetBytes(_username.ToUpper()));
		byte[] array16 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array12, array15, array4, bigInteger7.ToCleanByteArray(), bigInteger2.ToCleanByteArray(), _key.ToCleanByteArray());
		_m2 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(bigInteger7.ToCleanByteArray(), array16, array9);
		SendLogonProof(bigInteger7.ToCleanByteArray(), array16, new byte[20]);
	}

	private void SendLogonProof(byte[] A, byte[] M1, byte[] crc)
	{
		ByteBuffer byteBuffer = new ByteBuffer();
		byteBuffer.WriteUInt8(1);
		byteBuffer.WriteBytes(A);
		byteBuffer.WriteBytes(M1);
		byteBuffer.WriteBytes(crc);
		byteBuffer.WriteUInt8(0);
		byteBuffer.WriteUInt8(0);
		_debugTraceBreakpointHandler(byteBuffer);
		SendPacket(byteBuffer);
	}

	private void HandleLogonProof(ByteBuffer packet)
	{
		AuthResult authResult = (AuthResult)packet.ReadUInt8();
		if (authResult != 0)
		{
			Log.Print(LogType.Error, $"Login failed. Reason: {authResult}", "HandleLogonProof", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(authResult);
			return;
		}
		byte[] array = packet.ReadBytes(20u);
		if (Settings.ServerBuild < ClientVersionBuild.V2_0_3_6299)
		{
			packet.ReadUInt32();
		}
		else if (Settings.ServerBuild < ClientVersionBuild.V2_4_0_8089)
		{
			packet.ReadUInt32();
			packet.ReadUInt16();
		}
		else
		{
			packet.ReadUInt32();
			packet.ReadUInt32();
			packet.ReadUInt16();
		}
		bool flag = _m2 != null && _m2.Length == 20;
		int num = 0;
		while (flag && num < _m2.Length && (flag = _m2[num] == array[num]))
		{
			num++;
		}
		if (!flag)
		{
			Log.Print(LogType.Error, "Authentication failed!", "HandleLogonProof", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(AuthResult.FAIL_INTERNAL_ERROR);
		}
		else
		{
			Log.Print(LogType.Network, "Authentication succeeded!", "HandleLogonProof", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(AuthResult.SUCCESS);
		}
	}

	public void HandleReconnectChallenge(ByteBuffer packet)
	{
		packet.ReadUInt8();
		byte[] array = packet.ReadBytes(16u);
		packet.ReadBytes(16u);
		RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create();
		byte[] array2 = new byte[16];
		randomNumberGenerator.GetBytes(array2);
		byte[] r = Framework.Cryptography.HashAlgorithm.SHA1.Hash(Encoding.ASCII.GetBytes(_username), array2, array, GetSessionKey());
		byte[] r2 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(array2, new byte[20]);
		SendReconnectProof(array2, r, r2);
	}

	private void SendReconnectProof(byte[] R1, byte[] R2, byte[] R3)
	{
		ByteBuffer byteBuffer = new ByteBuffer();
		byteBuffer.WriteUInt8(3);
		byteBuffer.WriteBytes(R1);
		byteBuffer.WriteBytes(R2);
		byteBuffer.WriteBytes(R3);
		byteBuffer.WriteUInt8(0);
		SendPacket(byteBuffer);
	}

	public void HandleReconnectProof(ByteBuffer packet)
	{
		AuthResult authResult = (AuthResult)packet.ReadUInt8();
		if (authResult != 0)
		{
			Log.Print(LogType.Error, $"Reconnect failed. Reason: {authResult}", "HandleReconnectProof", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
			SetAuthResponse(authResult);
		}
		else
		{
			SetAuthResponse(AuthResult.SUCCESS);
		}
	}

	public void SendRealmListUpdateRequest()
	{
		Log.Print(LogType.Server, "Requesting RealmList update for " + _username, "SendRealmListUpdateRequest", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
		ByteBuffer byteBuffer = new ByteBuffer();
		byteBuffer.WriteUInt8(16);
		for (int i = 0; i < 4; i++)
		{
			byteBuffer.WriteUInt8(0);
		}
		_realmlistRequestIsPending = true;
		SendPacket(byteBuffer);
	}

	private void HandleRealmList(ByteBuffer packet)
	{
		packet.ReadUInt16();
		packet.ReadUInt32();
		ushort num = 0;
		num = ((Settings.ServerBuild >= ClientVersionBuild.V2_0_3_6299) ? packet.ReadUInt16() : packet.ReadUInt8());
		Log.Print(LogType.Network, $"Received {num} realms.", "HandleRealmList", "D:\\a\\HermesProxy\\HermesProxy\\Auth\\AuthClient.cs");
		List<RealmInfo> list = new List<RealmInfo>();
		for (ushort num2 = 0; num2 < num; num2 = (ushort)(num2 + 1))
		{
			RealmInfo realmInfo = new RealmInfo();
			realmInfo.ID = num2;
			if (Settings.ServerBuild < ClientVersionBuild.V2_0_3_6299)
			{
				realmInfo.Type = (RealmType)packet.ReadUInt32();
			}
			else
			{
				realmInfo.Type = (RealmType)packet.ReadUInt8();
				realmInfo.IsLocked = packet.ReadUInt8();
			}
			realmInfo.Flags = (RealmFlags)packet.ReadUInt8();
			realmInfo.Name = packet.ReadCString();
			string[] array = packet.ReadCString().Split(':');
			realmInfo.Address = Dns.GetHostAddresses(array[0].Trim()).First().ToString();
			realmInfo.Port = ushort.Parse(array[1]);
			realmInfo.Population = packet.ReadFloat();
			realmInfo.CharacterCount = packet.ReadUInt8();
			realmInfo.Timezone = packet.ReadUInt8();
			packet.ReadUInt8();
			if ((realmInfo.Flags & RealmFlags.SpecifyBuild) != 0)
			{
				realmInfo.VersionMajor = packet.ReadUInt8();
				realmInfo.VersionMinor = packet.ReadUInt8();
				realmInfo.VersonBugfix = packet.ReadUInt8();
				realmInfo.Build = packet.ReadUInt16();
			}
			list.Add(realmInfo);
		}
		GetSession().RealmManager.UpdateRealms(list);
		_hasRealmlist.SetResult();
	}

	public void WaitOrRequestRealmList()
	{
		if (!_realmlistRequestIsPending || !_hasRealmlist.Task.Wait(TimeSpan.FromSeconds(2.0)))
		{
			SendRealmListUpdateRequest();
		}
		_hasRealmlist.Task.Wait();
	}

	static AuthClient()
	{
		_debugTraceBreakpointHandler = delegate
		{
		};
		_debugTraceBreakpointHandler = delegate(ByteBuffer b)
		{
			if (Settings.ReportedOS == "OSX" && Settings.ReportedPlatform == "x86" && b.GetCurrentStream() is MemoryStream memoryStream)
			{
				MacOsPasswordHashFix(memoryStream.GetBuffer());
			}
		};
	}

	private static void MacOsPasswordHashFix(byte[] b)
	{
		byte[] array = LegacyVersion.BuildInt switch
		{
			5875 => new byte[20]
			{
				141, 23, 60, 195, 129, 150, 30, 235, 171, 243,
				54, 245, 230, 103, 91, 16, 27, 181, 19, 229
			}, 
			8606 => new byte[20]
			{
				216, 176, 236, 254, 83, 75, 193, 19, 30, 25,
				186, 209, 212, 192, 232, 19, 238, 228, 153, 79
			}, 
			_ => null, 
		};
		if (array != null)
		{
			byte[] subArray = b[1..33];
			byte[] array2 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(subArray, array);
			Array.Copy(array2, 0, b, 53, array2.Length);
		}
	}
}
