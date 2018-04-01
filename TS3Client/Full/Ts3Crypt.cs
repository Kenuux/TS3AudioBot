// TS3Client - A free TeamSpeak3 client implementation
// Copyright (C) 2017  TS3Client contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3Client.Full
{
	using Chaos.NaCl.Ed25519Ref10;
	using Commands;
	using Helper;
	using Org.BouncyCastle.Asn1;
	using Org.BouncyCastle.Asn1.X9;
	using Org.BouncyCastle.Crypto;
	using Org.BouncyCastle.Crypto.Digests;
	using Org.BouncyCastle.Crypto.Engines;
	using Org.BouncyCastle.Crypto.Generators;
	using Org.BouncyCastle.Crypto.Modes;
	using Org.BouncyCastle.Crypto.Parameters;
	using Org.BouncyCastle.Math;
	using Org.BouncyCastle.Math.EC;
	using Org.BouncyCastle.Security;
	using System;
	using System.Buffers.Binary;
	using System.Diagnostics;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Text;
	using System.Text.RegularExpressions;

	/// <summary>Provides all cryptographic functions needed for the low- and high level TeamSpeak protocol usage.</summary>
	public sealed class Ts3Crypt
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private const string DummyKeyAndNonceString = "c:\\windows\\system\\firewall32.cpl";
		private static readonly byte[] DummyKey = Encoding.ASCII.GetBytes(DummyKeyAndNonceString.Substring(0, 16));
		private static readonly byte[] DummyIv = Encoding.ASCII.GetBytes(DummyKeyAndNonceString.Substring(16, 16));
		private static readonly (byte[], byte[]) DummyKeyAndNonceTuple = (DummyKey, DummyIv);
		private static readonly byte[] Ts3InitMac = Encoding.ASCII.GetBytes("TS3INIT1");
		private static readonly byte[] Initversion = { 0x09, 0x83, 0x8C, 0xCF }; // 3.1.8 [Stable]
		private readonly EaxBlockCipher eaxCipher = new EaxBlockCipher(new AesEngine());
		private static readonly Regex IdentityRegex = new Regex(@"^(?<level>\d+)V(?<identity>[\w\/\+]+={0,2})$", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

		private const int MacLen = 8;
		private const int PacketTypeKinds = 9;

		public IdentityData Identity { get; set; }

		internal bool CryptoInitComplete { get; private set; }
		private byte[] alphaTmp;
		private byte[] ivStruct;
		private readonly byte[] fakeSignature = new byte[MacLen];
		private readonly (byte[] key, byte[] nonce, uint generation)?[] cachedKeyNonces = new(byte[], byte[], uint)?[PacketTypeKinds * 2];

		public Ts3Crypt()
		{
			Reset();
		}

		internal void Reset()
		{
			CryptoInitComplete = false;
			ivStruct = null;
			Array.Clear(fakeSignature, 0, fakeSignature.Length);
			Array.Clear(cachedKeyNonces, 0, cachedKeyNonces.Length);
			Identity = null;
		}

		#region KEY IMPORT/EXPROT

		/// <summary>
		/// Detects the kind of key and creates an identity from it.
		/// This method can import 3 kinds of identity keys.
		/// <list type="bullet">
		/// <item><description>The Teamspeak 3 key as it is stored by the normal client.</description></item>
		/// <item><description>A libtomcrypt public+private key export. (+KeyOffset).</description></item>
		/// <item><description>A TS3Client's private-only key export. (+KeyOffset).</description></item>
		/// </list>
		/// Keys with "(+KeyOffset)" should add the key offset for the security level in the seperate parameter.
		/// </summary>
		/// <param name="key">The identity string.</param>
		/// <param name="keyOffset">A number which determines the security level of an identity.</param>
		/// <param name="lastCheckedKeyOffset">The last brute forced number. Default 0: will take the current keyOffset.</param>
		/// <returns>The identity information.</returns>
		public static R<IdentityData> LoadIdentityDynamic(string key, ulong keyOffset = 0, ulong lastCheckedKeyOffset = 0)
		{
			var ts3identity = DeobfuscateAndImportTs3Identity(key);
			if (ts3identity.Ok)
				return ts3identity.Value;
			return LoadIdentity(key, keyOffset, lastCheckedKeyOffset);
		}

		/// <summary>This methods loads a secret identity.</summary>
		/// <param name="key">The key stored in base64, encoded like the libtomcrypt export method of a private key.
		/// Or the TS3Client's shorted private-only key.</param>
		/// <param name="keyOffset">A number which determines the security level of an identity.</param>
		/// <param name="lastCheckedKeyOffset">The last brute forced number. Default 0: will take the current keyOffset.</param>
		/// <returns>The identity information.</returns>
		public static R<IdentityData> LoadIdentity(string key, ulong keyOffset, ulong lastCheckedKeyOffset = 0)
		{
			// Note: libtomcrypt stores the private AND public key when exporting a private key
			// This makes importing very convenient :)
			var asnByteArray = Base64Decode(key);
			if (!asnByteArray.Ok)
				return "Invalid identity base64 string";
			var importRes = ImportKeyDynamic(asnByteArray.Value);
			if (!importRes.Ok)
				return importRes.Error;
			var (publicKey, privateKey) = importRes.Value;
			return LoadIdentity(publicKey, privateKey, keyOffset, lastCheckedKeyOffset);
		}

		private static IdentityData LoadIdentity(ECPoint publicKey, BigInteger privateKey, ulong keyOffset, ulong lastCheckedKeyOffset)
		{
			return new IdentityData(privateKey, publicKey)
			{
				ValidKeyOffset = keyOffset,
				LastCheckedKeyOffset = lastCheckedKeyOffset < keyOffset ? keyOffset : lastCheckedKeyOffset,
			};
		}

		private static readonly ECKeyGenerationParameters KeyGenParams = new ECKeyGenerationParameters(X9ObjectIdentifiers.Prime256v1, new SecureRandom());

		private static R<ECPoint> ImportPublicKey(byte[] asnByteArray)
		{
			try
			{
				var asnKeyData = (DerSequence)Asn1Object.FromByteArray(asnByteArray);
				var x = ((DerInteger)asnKeyData[2]).Value;
				var y = ((DerInteger)asnKeyData[3]).Value;

				var ecPoint = KeyGenParams.DomainParameters.Curve.CreatePoint(x, y);
				return ecPoint;
			}
			catch (Exception) { return "Could not import public key"; }
		}

		private static R<(ECPoint publicKey, BigInteger privateKey)> ImportKeyDynamic(byte[] asnByteArray)
		{
			BigInteger privateKey = null;
			ECPoint publicKey = null;
			try
			{
				var asnKeyData = (DerSequence)Asn1Object.FromByteArray(asnByteArray);
				var bitInfo = ((DerBitString)asnKeyData[0]).IntValue;
				if (bitInfo == 0b0000_0000 || bitInfo == 0b1000_0000)
				{
					var x = ((DerInteger)asnKeyData[2]).Value;
					var y = ((DerInteger)asnKeyData[3]).Value;
					publicKey = KeyGenParams.DomainParameters.Curve.CreatePoint(x, y);

					if (bitInfo == 0b1000_0000)
					{
						privateKey = ((DerInteger)asnKeyData[4]).Value;
					}
				}
				else if (bitInfo == 0b1100_0000)
				{
					privateKey = ((DerInteger)asnKeyData[2]).Value;
				}
			}
			catch (Exception ex) { return $"Could not import identity: {ex.Message}"; }
			return (publicKey, privateKey);
		}

		internal static string ExportPublicKey(ECPoint publicKey)
		{
			var dataArray = new DerSequence(
				new DerBitString(new byte[] { 0b0000_0000 }, 7),
				new DerInteger(32),
				new DerInteger(publicKey.AffineXCoord.ToBigInteger()),
				new DerInteger(publicKey.AffineYCoord.ToBigInteger())).GetDerEncoded();
			return Convert.ToBase64String(dataArray);
		}

		internal static string ExportPrivateKey(BigInteger privateKey)
		{
			var dataArray = new DerSequence(
				new DerBitString(new byte[] { 0b1100_0000 }, 6),
				new DerInteger(32),
				new DerInteger(privateKey)).GetDerEncoded();
			return Convert.ToBase64String(dataArray);
		}

		internal static string ExportPublicAndPrivateKey(ECPoint publicKey, BigInteger privateKey)
		{
			var dataArray = new DerSequence(
				new DerBitString(new byte[] { 0b1000_0000 }, 7),
				new DerInteger(32),
				new DerInteger(publicKey.AffineXCoord.ToBigInteger()),
				new DerInteger(publicKey.AffineYCoord.ToBigInteger()),
				new DerInteger(privateKey)).GetDerEncoded();
			return Convert.ToBase64String(dataArray);
		}

		internal static string GetUidFromPublicKey(string publicKey)
		{
			var publicKeyBytes = Encoding.ASCII.GetBytes(publicKey);
			var hashBytes = Hash1It(publicKeyBytes);
			return Convert.ToBase64String(hashBytes);
		}

		internal static ECPoint RestorePublicFromPrivateKey(BigInteger privateKey)
		{
			var curve = ECNamedCurveTable.GetByOid(X9ObjectIdentifiers.Prime256v1);
			return curve.G.Multiply(privateKey).Normalize();
		}

		private static readonly byte[] Ts3IdentityObfuscationKey = Encoding.ASCII.GetBytes("b9dfaa7bee6ac57ac7b65f1094a1c155e747327bc2fe5d51c512023fe54a280201004e90ad1daaae1075d53b7d571c30e063b5a62a4a017bb394833aa0983e6e");

		public static R<IdentityData> DeobfuscateAndImportTs3Identity(string identity)
		{
			var match = IdentityRegex.Match(identity);
			if (!match.Success)
				return "Identity could not get matched as teamspeak identity";

			if (!ulong.TryParse(match.Groups["level"].Value, out var level))
				return "Invalid key offset";

			var ident = Base64Decode(match.Groups["identity"].Value);
			if (!ident.Ok)
				return "Invalid identity base64 string";

			var identityArr = ident.Value;
			if (ident.Value.Length < 20)
				return "Identity too short";

			int nullIdx = identityArr.AsSpan().Slice(20).IndexOf((byte)0);
			var hash = Hash1It(identityArr, 20, nullIdx < 0 ? identityArr.Length - 20 : nullIdx);

			XorBinary(identityArr, hash, 20, identityArr);
			XorBinary(identityArr, Ts3IdentityObfuscationKey, Math.Min(100, identityArr.Length), identityArr);

			if (System.Buffers.Text.Base64.DecodeFromUtf8InPlace(identityArr, out var length) != System.Buffers.OperationStatus.Done)
				return "Invalid deobfuscated base64 string";

			var importRes = ImportKeyDynamic(identityArr.AsSpan().Slice(0, length).ToArray());
			if (!importRes.Ok)
				return importRes.Error;

			var (publicKey, privateKey) = importRes.Value;
			return LoadIdentity(publicKey, privateKey, level, level);
		}

		#endregion

		#region TS3INIT1 / CRYPTO INIT

		/// <summary>Calculates and initializes all required variables for the secure communication.</summary>
		/// <param name="alpha">The alpha key from clientinit encoded in base64.</param>
		/// <param name="beta">The beta key from clientinit encoded in base64.</param>
		/// <param name="omega">The omega key from clientinit encoded in base64.</param>
		internal R CryptoInit(string alpha, string beta, string omega)
		{
			if (Identity == null)
				throw new InvalidOperationException($"No identity has been imported or created. Use the {nameof(LoadIdentity)} or {nameof(GenerateNewIdentity)} method before.");

			var alphaBytes = Base64Decode(alpha);
			if (!alphaBytes.Ok) return "alpha parameter is invalid";
			var betaBytes = Base64Decode(beta);
			if (!alphaBytes.Ok) return "betaBytes parameter is invalid";
			var omegaBytes = Base64Decode(omega);
			if (!alphaBytes.Ok) return "omegaBytes parameter is invalid";
			var serverPublicKey = ImportPublicKey(omegaBytes.Value);
			if (!serverPublicKey.Ok) return "server public key is invalid";

			byte[] sharedKey = GetSharedSecret(serverPublicKey.Value);
			return SetSharedSecret(alphaBytes.Value, betaBytes.Value, sharedKey);
		}

		/// <summary>Calculates a shared secred with ECDH from the client private and server public key.</summary>
		/// <param name="publicKeyPoint">The public key of the server.</param>
		/// <returns>Returns a 32 byte shared secret.</returns>
		private byte[] GetSharedSecret(ECPoint publicKeyPoint)
		{
			ECPoint p = publicKeyPoint.Multiply(Identity.PrivateKey).Normalize();
			byte[] keyArr = p.AffineXCoord.ToBigInteger().ToByteArray();
			if (keyArr.Length == 32)
				return Hash1It(keyArr);
			if (keyArr.Length > 32)
				return Hash1It(keyArr, keyArr.Length - 32, 32);
			// else keyArr.Length < 32
			var keyArrExt = new byte[32];
			Array.Copy(keyArr, 0, keyArrExt, 32 - keyArr.Length, keyArr.Length);
			return Hash1It(keyArrExt);
		}

		/// <summary>Initializes all required variables for the secure communication.</summary>
		/// <param name="alpha">The alpha key from clientinit.</param>
		/// <param name="beta">The beta key from clientinit.</param>
		/// <param name="sharedKey">The omega key from clientinit.</param>
		private R SetSharedSecret(ReadOnlySpan<byte> alpha, ReadOnlySpan<byte> beta, ReadOnlySpan<byte> sharedKey)
		{
			if (beta.Length != 10 && beta.Length != 54)
				return $"Invalid beta size ({beta.Length})";

			// prepares the ivstruct consisting of 2 random byte chains of 10/10 or 10/54 bytes which both sides agreed on
			ivStruct = new byte[10 + beta.Length];

			// applying hashes to get the required values for ts3
			XorBinary(sharedKey, alpha, alpha.Length, ivStruct);
			XorBinary(sharedKey.Slice(10), beta, beta.Length, ivStruct.AsSpan().Slice(10));

			// creating a dummy signature which will be used on packets which dont use a real encryption signature (like plain voice)
			var buffer2 = Hash1It(ivStruct, 0, ivStruct.Length);
			Array.Copy(buffer2, 0, fakeSignature, 0, 8);

			alphaTmp = null;
			CryptoInitComplete = true;
			return R.OkR;
		}

		internal R CryptoInit2(string license, string omega, string proof, string beta, byte[] privateKey)
		{
			var licenseBytes = Base64Decode(license);
			if (!licenseBytes.Ok) return "license parameter is invalid";
			var omegaBytes = Base64Decode(omega);
			if (!omegaBytes.Ok) return "omega parameter is invalid";
			var proofBytes = Base64Decode(proof);
			if (!proofBytes.Ok) return "proof parameter is invalid";
			var betaBytes = Base64Decode(beta);
			if (!betaBytes.Ok) return "beta parameter is invalid";
			var serverPublicKey = ImportPublicKey(omegaBytes.Value);
			if (!serverPublicKey.Ok) return "server public key is invalid";

			// Verify that our connection isn't tampered with
			if (!VerifySign(serverPublicKey.Value, licenseBytes.Value, proofBytes.Value))
				return "The init proof is not valid. Your connection might be tampered with or the sever is an idiot.";

			var sw = Stopwatch.StartNew();
			var licenseChainR = Licenses.Parse(licenseBytes.Value);
			if (!licenseChainR.Ok)
				return licenseChainR.Error;
			Log.Debug("Parsed license successfully in {0:F3}ms", sw.Elapsed.TotalMilliseconds);

			var licenseChain = licenseChainR.Value;
			sw.Restart();
			var key = licenseChain.DeriveKey();
			Log.Debug("Processed license successfully in {0:F3}ms", sw.Elapsed.TotalMilliseconds);

			sw.Restart();
			var keyArr = GetSharedSecret2(key, privateKey);
			Log.Debug("Calculated shared secret in {0:F3}ms", sw.Elapsed.TotalMilliseconds);

			return SetSharedSecret(alphaTmp, betaBytes.Value, keyArr);
		}

		private static byte[] GetSharedSecret2(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey)
		{
			Span<byte> privateKeyCpy = stackalloc byte[32];
			privateKey.CopyTo(privateKeyCpy);
			privateKeyCpy[31] &= 0x7F;
			GroupOperations.ge_frombytes_negate_vartime(out var pub1, publicKey);
			GroupOperations.ge_scalarmult_vartime(out GroupElementP2 mul, privateKeyCpy, pub1);
			Span<byte> sharedTmp = stackalloc byte[32];
			GroupOperations.ge_tobytes(sharedTmp, mul);
			sharedTmp[31] ^= 0x80;
			var bytes = new byte[64];
			Chaos.NaCl.Sha512.Hash(sharedTmp, bytes);
			return bytes;
		}

		internal R<byte[]> ProcessInit1(byte[] data)
		{
			const int versionLen = 4;
			const int initTypeLen = 1;

			int? type = null;
			if (data != null)
			{
				type = data[0];
				if (data.Length < initTypeLen)
					return "Invalid Init1 packet (too short)";
			}
			byte[] sendData;

			switch (type)
			{
			case 0x7F:
			// 0x7F: Some strange servers do this
			// the normal client responds by starting again
			case null:
				sendData = new byte[versionLen + initTypeLen + 4 + 4 + 8];
				Array.Copy(Initversion, 0, sendData, 0, versionLen); // initVersion
				sendData[versionLen] = 0x00; // initType
				BinaryPrimitives.WriteUInt32BigEndian(sendData.AsSpan().Slice(versionLen + initTypeLen), Util.UnixNow);// 4byte timestamp
				for (int i = 0; i < 4; i++)
					sendData[i + versionLen + initTypeLen + 4] = (byte)Util.Random.Next(0, 256); // 4byte random
				return sendData;

			case 1:
				switch (data.Length)
				{
				case 21:
					sendData = new byte[versionLen + initTypeLen + 16 + 4];
					Array.Copy(Initversion, 0, sendData, 0, versionLen); // initVersion
					sendData[versionLen] = 0x02; // initType
					Array.Copy(data, 1, sendData, versionLen + initTypeLen, 20);
					return sendData;
				case 5:
					var errorNum = BinaryPrimitives.ReadUInt32LittleEndian(data.AsReadOnlySpan().Slice(1));
					if (Enum.IsDefined(typeof(Ts3ErrorCode), errorNum))
						return $"Got Init1(1) error: {(Ts3ErrorCode)errorNum}";
					return $"Got Init1(1) undefined error code: {errorNum}";
				default:
					return "Invalid or unrecognized Init1(1) packet";
				}

			case 3:
				alphaTmp = new byte[10];
				Util.Random.NextBytes(alphaTmp);
				var alpha = Convert.ToBase64String(alphaTmp);
				string initAdd = Ts3Command.BuildToString("clientinitiv",
					new ICommandPart[] {
						new CommandParameter("alpha", alpha),
						new CommandParameter("omega", Identity.PublicKeyString),
						new CommandParameter("ot", 1),
						new CommandParameter("ip", string.Empty) });
				var textBytes = Util.Encoder.GetBytes(initAdd);

				// Prepare solution
				int level = BinaryPrimitives.ReadInt32BigEndian(data.AsReadOnlySpan().Slice(initTypeLen + 128));
				var y = SolveRsaChallange(data, initTypeLen, level);
				if (!y.Ok)
					return y;

				// Copy bytes for this result: [Version..., InitType..., data..., y..., text...]
				sendData = new byte[versionLen + initTypeLen + 232 + 64 + textBytes.Length];
				// Copy this.Version
				Array.Copy(Initversion, 0, sendData, 0, versionLen);
				// Write InitType
				sendData[versionLen] = 0x04;
				// Copy data
				Array.Copy(data, initTypeLen, sendData, versionLen + initTypeLen, 232);
				// Copy y
				Array.Copy(y.Value, 0, sendData, versionLen + initTypeLen + 232 + (64 - y.Value.Length), y.Value.Length);
				// Copy text
				Array.Copy(textBytes, 0, sendData, versionLen + initTypeLen + 232 + 64, textBytes.Length);
				return sendData;

			default:
				return $"Got invalid Init1({type}) packet id";
			}
		}

		/// <summary>This method calculates x ^ (2^level) % n = y which is the solution to the server RSA puzzle.</summary>
		/// <param name="data">The data array, containing x=[0,63] and n=[64,127], each unsigned, as a BigInteger bytearray.</param>
		/// <param name="offset">The offset of x and n in the data array.</param>
		/// <param name="level">The exponent to x.</param>
		/// <returns>The y value, unsigned, as a BigInteger bytearray.</returns>
		private static R<byte[]> SolveRsaChallange(byte[] data, int offset, int level)
		{
			if (level < 0 || level > 1_000_000)
				return "RSA challange level is not within an acceptable range";

			// x is the base, n is the modulus.
			var x = new BigInteger(1, data, 00 + offset, 64);
			var n = new BigInteger(1, data, 64 + offset, 64);
			return x.ModPow(BigInteger.Two.Pow(level), n).ToByteArrayUnsigned();
		}

		internal static (byte[] publicKey, byte[] privateKey) GenerateTemporaryKey()
		{
			var privateKey = new byte[32];
			using (var rng = RandomNumberGenerator.Create())
				rng.GetBytes(privateKey);
			ScalarOperations.sc_clamp(privateKey);

			GroupOperations.ge_scalarmult_base(out var A, privateKey);
			var publicKey = new byte[32];
			GroupOperations.ge_p3_tobytes(publicKey, A);

			return (publicKey, privateKey);
		}

		#endregion

		#region ENCRYPTION/DECRYPTION

		internal void Encrypt(BasePacket packet)
		{
			if (packet.PacketType == PacketType.Init1)
			{
				FakeEncrypt(packet, Ts3InitMac);
				return;
			}
			if (packet.UnencryptedFlag)
			{
				FakeEncrypt(packet, fakeSignature);
				return;
			}

			var (key, nonce) = GetKeyNonce(packet.FromServer, packet.PacketId, packet.GenerationId, packet.PacketType);
			packet.BuildHeader();
			ICipherParameters ivAndKey = new AeadParameters(new KeyParameter(key), 8 * MacLen, nonce, packet.Header);

			byte[] result;
			int len;
			lock (eaxCipher)
			{
				eaxCipher.Init(true, ivAndKey);
				result = new byte[eaxCipher.GetOutputSize(packet.Size)];
				try
				{
					len = eaxCipher.ProcessBytes(packet.Data, 0, packet.Size, result, 0);
					len += eaxCipher.DoFinal(result, len);
				}
				catch (Exception ex) { throw new Ts3Exception("Internal encryption error.", ex); }
			}

			// result consists of [Data..., Mac...]
			// to build the final TS3/libtomcrypt we need to copy it into another order

			// len is Data.Length + Mac.Length
			packet.Raw = new byte[packet.HeaderLength + len];
			// Copy the Mac from [Data..., Mac...] to [Mac..., Header..., Data...]
			Array.Copy(result, len - MacLen, packet.Raw, 0, MacLen);
			// Copy the Header from packet.Header to [Mac..., Header..., Data...]
			Array.Copy(packet.Header, 0, packet.Raw, MacLen, packet.HeaderLength);
			// Copy the Data from [Data..., Mac...] to [Mac..., Header..., Data...]
			Array.Copy(result, 0, packet.Raw, MacLen + packet.HeaderLength, len - MacLen);
			// Raw is now [Mac..., Header..., Data...]
		}

		private static void FakeEncrypt(BasePacket packet, byte[] mac)
		{
			packet.Raw = new byte[packet.Data.Length + MacLen + packet.HeaderLength];
			// Copy the Mac from [Mac...] to [Mac..., Header..., Data...]
			Array.Copy(mac, 0, packet.Raw, 0, MacLen);
			// Copy the Header from packet.Header to [Mac..., Header..., Data...]
			packet.BuildHeader(packet.Raw.AsSpan().Slice(MacLen, packet.HeaderLength));
			// Copy the Data from packet.Data to [Mac..., Header..., Data...]
			Array.Copy(packet.Data, 0, packet.Raw, MacLen + packet.HeaderLength, packet.Data.Length);
			// Raw is now [Mac..., Header..., Data...]
		}

		internal static S2CPacket GetS2CPacket(byte[] data)
		{
			if (data.Length < S2CPacket.HeaderLen + MacLen)
				return null;

			return new S2CPacket(data)
			{
				PacketTypeFlagged = data[MacLen + 2],
				PacketId = BinaryPrimitives.ReadUInt16BigEndian(data.AsReadOnlySpan().Slice(MacLen)),
			};
		}

		internal static C2SPacket GetC2SPacket(byte[] data)
		{
			if (data.Length < C2SPacket.HeaderLen + MacLen)
				return null;
			// TODO standartize packet direction generation see s2c/c2s
			return new C2SPacket(null, 0)
			{
				Raw = data,
				PacketTypeFlagged = data[MacLen + 4],
				PacketId = BinaryPrimitives.ReadUInt16BigEndian(data.AsReadOnlySpan().Slice(MacLen)),
			};
		}

		internal bool Decrypt(BasePacket packet)
		{
			if (packet.PacketType == PacketType.Init1)
				return FakeDecrypt(packet, Ts3InitMac);

			if (packet.UnencryptedFlag)
				return FakeDecrypt(packet, fakeSignature);

			return DecryptData(packet);
		}

		private bool DecryptData(BasePacket packet)
		{
			Array.Copy(packet.Raw, MacLen, packet.Header, 0, packet.HeaderLength);
			var (key, nonce) = GetKeyNonce(packet.FromServer, packet.PacketId, packet.GenerationId, packet.PacketType);
			int dataLen = packet.Raw.Length - (MacLen + packet.HeaderLength);

			ICipherParameters ivAndKey = new AeadParameters(new KeyParameter(key), 8 * MacLen, nonce, packet.Header);
			try
			{
				byte[] result;
				lock (eaxCipher)
				{
					eaxCipher.Init(false, ivAndKey);
					result = new byte[eaxCipher.GetOutputSize(dataLen + MacLen)];

					int len = eaxCipher.ProcessBytes(packet.Raw, MacLen + packet.HeaderLength, dataLen, result, 0);
					len += eaxCipher.ProcessBytes(packet.Raw, 0, MacLen, result, len);
					len += eaxCipher.DoFinal(result, len);

					if (len != dataLen)
						return false;
				}

				packet.Data = result;
			}
			catch (Exception) { return false; }
			return true;
		}

		private static bool FakeDecrypt(BasePacket packet, byte[] mac)
		{
			if (!CheckEqual(packet.Raw, mac, MacLen))
				return false;
			int dataLen = packet.Raw.Length - (MacLen + packet.HeaderLength);
			packet.Data = new byte[dataLen];
			Array.Copy(packet.Raw, MacLen + packet.HeaderLength, packet.Data, 0, dataLen);
			return true;
		}

		/// <summary>TS3 uses a new key and nonce for each packet sent and received. This method generates and caches these.</summary>
		/// <param name="fromServer">True if the packet is from server to client, false for client to server.</param>
		/// <param name="packetId">The id of the packet, host order.</param>
		/// <param name="generationId">Each time the packetId reaches 65535 the next packet will go on with 0 and the generationId will be increased by 1.</param>
		/// <param name="packetType">The packetType.</param>
		/// <returns>A tuple of (key, nonce)</returns>
		private (byte[] key, byte[] nonce) GetKeyNonce(bool fromServer, ushort packetId, uint generationId, PacketType packetType)
		{
			if (!CryptoInitComplete)
				return DummyKeyAndNonceTuple;

			// only the lower 4 bits are used for the real packetType
			byte packetTypeRaw = (byte)packetType;

			int cacheIndex = packetTypeRaw * (fromServer ? 1 : 2);
			if (!cachedKeyNonces[cacheIndex].HasValue || cachedKeyNonces[cacheIndex].Value.generation != generationId)
			{
				// this part of the key/nonce is fixed by the message direction and packetType

				byte[] tmpToHash = new byte[ivStruct.Length == 20 ? 26 : 70];

				tmpToHash[0] = fromServer ? (byte)0x30 : (byte)0x31;
				tmpToHash[1] = packetTypeRaw;

				BinaryPrimitives.WriteUInt32BigEndian(tmpToHash.AsSpan().Slice(2), generationId);
				Array.Copy(ivStruct, 0, tmpToHash, 6, ivStruct.Length);

				var result = Hash256It(tmpToHash).AsSpan();

				cachedKeyNonces[cacheIndex] = (result.Slice(0, 16).ToArray(), result.Slice(16, 16).ToArray(), generationId);
			}

			byte[] key = new byte[16];
			byte[] nonce = new byte[16];
			Array.Copy(cachedKeyNonces[cacheIndex].Value.key, 0, key, 0, 16);
			Array.Copy(cachedKeyNonces[cacheIndex].Value.nonce, 0, nonce, 0, 16);

			// finally the first two bytes get xor'd with the packet id
			key[0] ^= unchecked((byte)(packetId >> 8));
			key[1] ^= unchecked((byte)(packetId >> 0));

			return (key, nonce);
		}

		#endregion

		#region CRYPT HELPER

		private static bool CheckEqual(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2, int len)
		{
			if (a1.Length < len || a2.Length < len)
				throw new ArgumentOutOfRangeException();

			int res = 0;
			for (int i = 0; i < len; i++)
				res |= a1[i] ^ a2[i];
			return res == 0;
		}

		private static void XorBinary(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int len, Span<byte> outBuf)
		{
			if (a.Length < len || b.Length < len || outBuf.Length < len) throw new ArgumentException();
			for (int i = 0; i < len; i++)
				outBuf[i] = (byte)(a[i] ^ b[i]);
		}

		private static readonly SHA1Managed Sha1HashInternal = new SHA1Managed();
		private static readonly Sha256Digest Sha256Hash = new Sha256Digest();
		private static readonly Sha512Digest Sha512Hash = new Sha512Digest();
		internal static byte[] Hash1It(byte[] data, int offset = 0, int len = 0) => HashItInternal(Sha1HashInternal, data, offset, len);
		internal static byte[] Hash256It(byte[] data, int offset = 0, int len = 0) => HashIt(Sha256Hash, data, offset, len);
		internal static byte[] Hash512It(byte[] data, int offset = 0, int len = 0) => HashIt(Sha512Hash, data, offset, len);
		private static byte[] HashItInternal(HashAlgorithm hashAlgo, byte[] data, int offset = 0, int len = 0)
		{
			lock (hashAlgo)
			{
				return hashAlgo.ComputeHash(data, offset, len == 0 ? data.Length - offset : len);
			}
		}
		private static byte[] HashIt(IDigest hashAlgo, byte[] data, int offset = 0, int len = 0)
		{
			byte[] result;
			lock (hashAlgo)
			{
				hashAlgo.Reset();
				hashAlgo.BlockUpdate(data, offset, len == 0 ? data.Length - offset : len);
				result = new byte[hashAlgo.GetDigestSize()];
				hashAlgo.DoFinal(result, 0);
			}
			return result;
		}

		public static string HashPassword(string password)
		{
			if (string.IsNullOrEmpty(password))
				return string.Empty;
			var bytes = Util.Encoder.GetBytes(password);
			var hashed = Hash1It(bytes);
			return Convert.ToBase64String(hashed);
		}

		public static byte[] Sign(BigInteger privateKey, byte[] data)
		{
			var signer = SignerUtilities.GetSigner(X9ObjectIdentifiers.ECDsaWithSha256);
			var signKey = new ECPrivateKeyParameters(privateKey, KeyGenParams.DomainParameters);
			signer.Init(true, signKey);
			signer.BlockUpdate(data, 0, data.Length);
			return signer.GenerateSignature();
		}

		public static bool VerifySign(ECPoint publicKey, byte[] data, byte[] proof)
		{
			var signer = SignerUtilities.GetSigner(X9ObjectIdentifiers.ECDsaWithSha256);
			var signKey = new ECPublicKeyParameters(publicKey, KeyGenParams.DomainParameters);
			signer.Init(false, signKey);
			signer.BlockUpdate(data, 0, data.Length);
			return signer.VerifySignature(proof);
		}

		public static readonly byte[] Ts3VerionSignPublicKey = Convert.FromBase64String("UrN1jX0dBE1vulTNLCoYwrVpfITyo+NBuq/twbf9hLw=");

		public static bool EdCheck(VersionSign sign)
		{
			var ver = Encoding.ASCII.GetBytes(sign.PlattformName + sign.Name);
			return Chaos.NaCl.Ed25519.Verify(Convert.FromBase64String(sign.Sign), ver, Ts3VerionSignPublicKey);
		}

		public static void VersionSelfCheck()
		{
			var versions = typeof(VersionSign).GetProperties().Where(prop => prop.PropertyType == typeof(VersionSign));
			foreach (var ver in versions)
			{
				var verObj = (VersionSign)ver.GetValue(null);
				if (!EdCheck(verObj))
					throw new Exception($"Version is invalid: {verObj}");
			}
		}

		private static R<byte[]> Base64Decode(string str)
		{
			try { return Convert.FromBase64String(str); }
			catch (FormatException) { return "Malformed base64 string"; }
		}

		#endregion

		#region IDENTITY & SECURITY LEVEL

		/// <summary>Equals ulong.MaxValue.ToString().Length</summary>
		private const int MaxUlongStringLen = 20;

		/// <summary><para>Tries to improve the security level of the provided identity to the new level.</para>
		/// <para>The algorithm takes approximately 2^toLevel milliseconds to calculate; so be careful!</para>
		/// This method can be canceled anytime since progress which is not enough for the next level
		/// will be saved in <see cref="IdentityData.LastCheckedKeyOffset"/> continuously.</summary>
		/// <param name="identity">The identity to improve.</param>
		/// <param name="toLevel">The targeted level.</param>
		public static void ImproveSecurity(IdentityData identity, int toLevel)
		{
			byte[] hashBuffer = new byte[identity.PublicKeyString.Length + MaxUlongStringLen];
			byte[] pubKeyBytes = Encoding.ASCII.GetBytes(identity.PublicKeyString);
			Array.Copy(pubKeyBytes, 0, hashBuffer, 0, pubKeyBytes.Length);

			identity.LastCheckedKeyOffset = Math.Max(identity.ValidKeyOffset, identity.LastCheckedKeyOffset);
			int best = GetSecurityLevel(hashBuffer, pubKeyBytes.Length, identity.ValidKeyOffset);
			while (true)
			{
				if (best >= toLevel) return;

				int curr = GetSecurityLevel(hashBuffer, pubKeyBytes.Length, identity.LastCheckedKeyOffset);
				if (curr > best)
				{
					identity.ValidKeyOffset = identity.LastCheckedKeyOffset;
					best = curr;
				}
				identity.LastCheckedKeyOffset++;
			}
		}

		public static int GetSecurityLevel(IdentityData identity)
		{
			byte[] hashBuffer = new byte[identity.PublicKeyString.Length + MaxUlongStringLen];
			byte[] pubKeyBytes = Encoding.ASCII.GetBytes(identity.PublicKeyString);
			Array.Copy(pubKeyBytes, 0, hashBuffer, 0, pubKeyBytes.Length);
			return GetSecurityLevel(hashBuffer, pubKeyBytes.Length, identity.ValidKeyOffset);
		}

		/// <summary>Creates a new TeamSpeak3 identity.</summary>
		/// <param name="securityLevel">Minimum security level this identity will have.</param>
		/// <returns>The identity information.</returns>
		public static IdentityData GenerateNewIdentity(int securityLevel = 8)
		{
			var ecp = ECNamedCurveTable.GetByName("prime256v1");
			var domainParams = new ECDomainParameters(ecp.Curve, ecp.G, ecp.N, ecp.H, ecp.GetSeed());
			var keyGenParams = new ECKeyGenerationParameters(domainParams, new SecureRandom());
			var generator = new ECKeyPairGenerator();
			generator.Init(keyGenParams);
			var keyPair = generator.GenerateKeyPair();

			var privateKey = (ECPrivateKeyParameters)keyPair.Private;
			var publicKey = (ECPublicKeyParameters)keyPair.Public;

			var identity = LoadIdentity(publicKey.Q.Normalize(), privateKey.D, 0, 0);
			ImproveSecurity(identity, securityLevel);
			return identity;
		}

		private static int GetSecurityLevel(byte[] hashBuffer, int pubKeyLen, ulong offset)
		{
			var numBuffer = new byte[MaxUlongStringLen];
			int numLen = 0;
			do
			{
				numBuffer[numLen] = (byte)('0' + (offset % 10));
				offset /= 10;
				numLen++;
			} while (offset > 0);
			for (int i = 0; i < numLen; i++)
				hashBuffer[pubKeyLen + i] = numBuffer[numLen - (i + 1)];
			byte[] outHash = Hash1It(hashBuffer, 0, pubKeyLen + numLen);

			return GetLeadingZeroBits(outHash);
		}

		private static int GetLeadingZeroBits(byte[] data)
		{
			int curr = 0;
			int i;
			for (i = 0; i < data.Length; i++)
				if (data[i] == 0) curr += 8;
				else break;
			if (i < data.Length)
				for (int bit = 0; bit < 8; bit++)
					if ((data[i] & (1 << bit)) == 0) curr++;
					else break;
			return curr;
		}

		/// <summary>
		/// This is the reference function from the TS3 Server for checking if a hashcash offset
		/// is sufficient for the required level.
		/// </summary>
		/// <param name="data">The sha1 result from the current offset calculation</param>
		/// <param name="reqLevel">The required level to reach.</param>
		/// <returns>True if the hash meets the requirement, false otherwise.</returns>
		private static bool ValidateHash(byte[] data, int reqLevel)
		{
			var levelMask = 1 << (reqLevel % 8) - 1;

			if (reqLevel < 8)
			{
				return (data[0] & levelMask) == 0;
			}
			else
			{
				var v9 = reqLevel / 8;
				var v10 = 0;
				while (data[v10] == 0)
				{
					if (++v10 >= v9)
					{
						return (data[v9] & levelMask) == 0;
					}
				}
				return false;
			}
		}

		#endregion

		enum CryptoVer
		{
			Unknown,
			/// <summary>
			/// Supported on server &lt;3.1.
			/// Supported on all clients.</summary>
			Version1 = 1,
			/// <summary>
			/// Supported on server &gt;=3.1.
			/// Supported on clients &gt;=3.1.6.</summary>
			Version2 = 2,
		}
	}
}
