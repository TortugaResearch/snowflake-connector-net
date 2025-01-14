﻿using System.Globalization;
using System.Security.Cryptography;
using Tortuga.Data.Snowflake.Core.Messages;

namespace Tortuga.Data.Snowflake.Core.FileTransfer;

/// <summary>
/// The encryptor/decryptor for PUT/GET files.
/// </summary>
static class EncryptionProvider
{
	// The default block size for AES
	const int AES_BLOCK_SIZE = 128;

	const int blockSize = AES_BLOCK_SIZE / 8;  // in bytes

	/// <summary>
	/// Encrypt data and write to the outStream.
	/// </summary>
	/// <param name="inFile">The data to write onto the stream.</param>
	/// <param name="encryptionMaterial">Contains the query stage master key, query id, and smk id.</param>
	/// <param name="encryptionMetadata">Store the encryption metadata into</param>
	/// <returns>The encrypted bytes of the file to upload.</returns>
	static public byte[] EncryptFile(string inFile, PutGetEncryptionMaterial encryptionMaterial, SFEncryptionMetadata encryptionMetadata)
	{
		var decodedMasterKey = Convert.FromBase64String(encryptionMaterial.queryStageMasterKey!);
		var masterKeySize = decodedMasterKey.Length;

		// Generate file key
		var keyData = new byte[blockSize];

		using var random = RandomNumberGenerator.Create();
		random.GetBytes(keyData);

		// Byte[] to encrypt data into
		var encryptedBytes = CreateEncryptedBytes(inFile, keyData, out var ivData);

		// Encrypt file key
		var encryptedFileKey = EncryptFileKey(decodedMasterKey, keyData);

		// Store encryption metadata information
		var matDesc = new MaterialDescriptor
		{
			SmkId = encryptionMaterial.smkId.ToString(CultureInfo.InvariantCulture),
			QueryId = encryptionMaterial.queryId,
			KeySize = (masterKeySize * 8).ToString(CultureInfo.InvariantCulture)
		};

		encryptionMetadata.iv = Convert.ToBase64String(ivData);
		encryptionMetadata.key = Convert.ToBase64String(encryptedFileKey);
		encryptionMetadata.matDesc = Newtonsoft.Json.JsonConvert.SerializeObject(matDesc).ToString();

		return encryptedBytes;
	}

	/// <summary>
	/// Encrypt the newly generated file key using the master key.
	/// </summary>
	/// <param name="masterKey">The key to use for encryption.</param>
	/// <param name="unencryptedFileKey">The file key to encrypt.</param>
	/// <returns>The encrypted key.</returns>
	static byte[] EncryptFileKey(byte[] masterKey, byte[] unencryptedFileKey)
	{
		using (var aes = Aes.Create())
		{
			aes.Key = masterKey;
#pragma warning disable CA5358 // Review cipher mode usage with cryptography experts
			aes.Mode = CipherMode.ECB;
#pragma warning restore CA5358 // Review cipher mode usage with cryptography experts
			aes.Padding = PaddingMode.PKCS7;

			using (var cipherStream = new MemoryStream())
			using (var cryptoStream = new CryptoStream(cipherStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
			{
				cryptoStream.Write(unencryptedFileKey, 0, unencryptedFileKey.Length);
				cryptoStream.FlushFinalBlock();

				return cipherStream.ToArray();
			}
		}
	}

	/// <summary>
	/// Creates a new byte array containing the encrypted/decrypted data.
	/// </summary>
	/// <param name="inFile">The path of the file to write onto the stream.</param>
	/// <param name="key">The encryption key.</param>
	/// <param name="iv">The encryption IV or null if it needs to be generated.</param>
	/// <returns>The encrypted bytes.</returns>
	static byte[] CreateEncryptedBytes(string inFile, byte[] key, out byte[] iv)
	{
		using var aes = Aes.Create();
		aes.Key = key;
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;
		iv = aes.IV;

		using (var targetStream = new MemoryStream())
		using (var cryptoStream = new CryptoStream(targetStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
		{
			var inFileBytes = File.ReadAllBytes(inFile);
			cryptoStream.Write(inFileBytes, 0, inFileBytes.Length);
			cryptoStream.FlushFinalBlock();

			return targetStream.ToArray();
		}
	}

	/// <summary>
	/// Decrypt data and write to the outStream.
	/// </summary>
	/// <param name="inFile">The data to write onto the stream.</param>
	/// <param name="encryptionMaterial">Contains the query stage master key, query id, and smk id.</param>
	/// <param name="encryptionMetadata">Store the encryption metadata into</param>
	/// <returns>The encrypted bytes of the file to upload.</returns>
	static public string DecryptFile(string inFile, PutGetEncryptionMaterial encryptionMaterial, SFEncryptionMetadata encryptionMetadata)
	{
		// Get key and iv from metadata
		var keyBase64 = encryptionMetadata.key ?? throw new ArgumentException("encryptionMetadata.key is null", nameof(encryptionMaterial));
		var ivBase64 = encryptionMetadata.iv ?? throw new ArgumentException("encryptionMetadata.iv is null", nameof(encryptionMaterial));

		// Get decoded key from base64 encoded value
		var decodedMasterKey = Convert.FromBase64String(encryptionMaterial.queryStageMasterKey!);

		// Get key bytes and iv bytes from base64 encoded value
		var keyBytes = Convert.FromBase64String(keyBase64);
		var ivBytes = Convert.FromBase64String(ivBase64);

		// Create temp file
		var tempFileName = Path.Combine(Path.GetTempPath(), Path.GetFileName(inFile));

		// Create decipher with file key, iv bytes, and AES CBC
		var decryptedFileKey = DecryptFileKey(decodedMasterKey, keyBytes);

		// Create key decipher with decoded key and AES ECB
		var decryptedBytes = CreateDecryptedBytes(inFile, decryptedFileKey, ivBytes);

		File.WriteAllBytes(tempFileName, decryptedBytes);

		return tempFileName;
	}

	/// <summary>
	/// Decrypt the newly generated file key using the master key.
	/// </summary>
	/// <param name="masterKey">The key to use for encryption.</param>
	/// <param name="unencryptedFileKey">The file key to encrypt.</param>
	/// <returns>The encrypted key.</returns>
	static byte[] DecryptFileKey(byte[] masterKey, byte[] unencryptedFileKey)
	{
		using var aes = Aes.Create();
		aes.Key = masterKey;
#pragma warning disable CA5358 // Review cipher mode usage with cryptography experts
		aes.Mode = CipherMode.ECB;
#pragma warning restore CA5358 // Review cipher mode usage with cryptography experts
		aes.Padding = PaddingMode.PKCS7;

		using (var cipherStream = new MemoryStream())
		using (var cryptoStream = new CryptoStream(cipherStream, aes.CreateDecryptor(), CryptoStreamMode.Write))
		{
			cryptoStream.Write(unencryptedFileKey, 0, unencryptedFileKey.Length);
			cryptoStream.FlushFinalBlock();

			return cipherStream.ToArray();
		}
	}

	/// <summary>
	/// Creates a new byte array containing the decrypted data.
	/// </summary>
	/// <param name="inFile">The path of the file to write onto the stream.</param>
	/// <param name="key">The encryption key.</param>
	/// <param name="iv">The encryption IV or null if it needs to be generated.</param>
	/// <returns>The encrypted bytes.</returns>
	static byte[] CreateDecryptedBytes(string inFile, byte[] key, byte[] iv)
	{
		using var aes = Aes.Create();
		aes.Key = key;
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;
		aes.IV = iv;

		using (var targetStream = new MemoryStream())
		using (var cryptoStream = new CryptoStream(targetStream, aes.CreateDecryptor(), CryptoStreamMode.Write))
		{
			using (var inStream = File.OpenRead(inFile))
			{
				var buffer = new byte[2048];
				int bytesRead;
				while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
				{
					cryptoStream.Write(buffer, 0, bytesRead);
				}
			}
			cryptoStream.FlushFinalBlock();

			return targetStream.ToArray();
		}
	}
}
