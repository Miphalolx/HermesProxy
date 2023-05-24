namespace Framework.Cryptography;

public sealed class Sha1
{
	private byte[] _s;

	private byte _tmp;

	private byte _tmp2;

	public Sha1()
	{
		_s = new byte[256];
		_tmp = 0;
		_tmp2 = 0;
	}

	public void SetBase(byte[] key)
	{
		for (int i = 0; i < 256; i++)
		{
			_s[i] = (byte)i;
		}
		int num = 0;
		for (int j = 0; j < 256; j++)
		{
			num = (byte)((num + key[j % key.Length] + _s[j]) & 0xFF);
			ref byte reference = ref _s[j];
			ref byte reference2 = ref _s[num];
			byte b = _s[num];
			byte b2 = _s[j];
			reference = b;
			reference2 = b2;
		}
	}

	public void ProcessBuffer(byte[] data, int length)
	{
		for (int i = 0; i < length; i++)
		{
			_tmp = (byte)((_tmp + 1) % 256);
			_tmp2 = (byte)((_tmp2 + _s[_tmp]) % 256);
			ref byte reference = ref _s[_tmp];
			ref byte reference2 = ref _s[_tmp2];
			byte b = _s[_tmp2];
			byte b2 = _s[_tmp];
			reference = b;
			reference2 = b2;
			data[i] = (byte)(_s[(_s[_tmp] + _s[_tmp2]) % 256] ^ data[i]);
		}
	}
}
