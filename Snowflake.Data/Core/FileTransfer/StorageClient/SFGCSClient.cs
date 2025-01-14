﻿/*
 * Copyright (c) 2021 Snowflake Computing Inc. All rights reserved.
 */

using Google.Apis.Auth.OAuth2;
using Newtonsoft.Json;
using System.Globalization;
using System.Net.Http.Headers;
using Tortuga.Data.Snowflake.Legacy;
using Tortuga.HttpClientUtilities;

namespace Tortuga.Data.Snowflake.Core.FileTransfer.StorageClient;

/// <summary>
/// The GCS client used to transfer files to the remote Google Cloud Storage.
/// </summary>
class SFGCSClient : ISFRemoteStorageClient, IDisposable
{
	/// <summary>
	/// GCS header values.
	/// </summary>
	const string GCS_METADATA_PREFIX = "x-goog-meta-";

	const string GCS_METADATA_SFC_DIGEST = GCS_METADATA_PREFIX + "sfc-digest";
	const string GCS_METADATA_MATDESC_KEY = GCS_METADATA_PREFIX + "matdesc";
	const string GCS_METADATA_ENCRYPTIONDATAPROP = GCS_METADATA_PREFIX + "encryptiondata";
	const string GCS_FILE_HEADER_CONTENT_LENGTH = "x-goog-stored-content-length";

	/// <summary>
	/// The attribute in the credential map containing the access token.
	/// </summary>
	const string GCS_ACCESS_TOKEN = "GCS_ACCESS_TOKEN";

	/// <summary>
	/// The storage client.
	/// </summary>
	readonly Google.Cloud.Storage.V1.StorageClient m_StorageClient;

	/// <summary>
	/// The HTTP client to make requests.
	/// </summary>
	readonly HttpClient m_HttpClient;

	/// <summary>
	/// GCS client with access token.
	/// </summary>
	/// <param name="stageInfo">The command stage info.</param>
	public SFGCSClient(PutGetStageInfo stageInfo)
	{
		if (stageInfo.StageCredentials == null)
			throw new ArgumentException("stageInfo.stageCredentials is null", nameof(stageInfo));

		if (stageInfo.StageCredentials.TryGetValue(GCS_ACCESS_TOKEN, out var accessToken))
		{
			var creds = GoogleCredential.FromAccessToken(accessToken, null);
			m_StorageClient = Google.Cloud.Storage.V1.StorageClient.Create(creds);
		}
		else
		{
			m_StorageClient = Google.Cloud.Storage.V1.StorageClient.CreateUnauthenticated();
		}

		m_HttpClient = new HttpClient();
	}

	RemoteLocation ISFRemoteStorageClient.ExtractBucketNameAndPath(string stageLocation) => ExtractBucketNameAndPath(stageLocation);

	/// <summary>
	/// Extract the bucket name and path from the stage location.
	/// </summary>
	/// <param name="stageLocation">The command stage location.</param>
	/// <returns>The remote location of the GCS file.</returns>
	static public RemoteLocation ExtractBucketNameAndPath(string stageLocation)
	{
		var containerName = stageLocation;
		var gcsPath = "";

		// Split stage location as bucket name and path
		if (stageLocation.Contains('/', StringComparison.Ordinal))
		{
			containerName = stageLocation.Substring(0, stageLocation.IndexOf('/', StringComparison.Ordinal));

			gcsPath = stageLocation.Substring(stageLocation.IndexOf('/', StringComparison.Ordinal) + 1,
				stageLocation.Length - stageLocation.IndexOf('/', StringComparison.Ordinal) - 1);
			if (gcsPath != null && !gcsPath.EndsWith("/", StringComparison.Ordinal))
			{
				gcsPath += '/';
			}
		}

		return new RemoteLocation()
		{
			Bucket = containerName,
			Key = gcsPath
		};
	}

	/// <summary>
	/// Get the file header.
	/// </summary>
	/// <param name="fileMetadata">The GCS file metadata.</param>
	/// <returns>The file header of the GCS file.</returns>
	public FileHeader? GetFileHeader(SFFileMetadata fileMetadata)
	{
		// If file already exists, return
		if (fileMetadata.ResultStatus == ResultStatus.UPLOADED.ToString() ||
			fileMetadata.ResultStatus == ResultStatus.DOWNLOADED.ToString())
		{
			return new FileHeader
			{
				digest = fileMetadata.Sha256Digest,
				contentLength = fileMetadata.SrcFileSize,
				encryptionMetadata = fileMetadata.EncryptionMetadata
			};
		}

		if (fileMetadata.PresignedUrl != null)
		{
			// Issue GET request to GCS file URL
			try
			{
				var response = m_HttpClient.GetStream(fileMetadata.PresignedUrl);
			}
			catch (HttpRequestException err)
			{
				if (err.Message.Contains("401", StringComparison.Ordinal) ||
					err.Message.Contains("403", StringComparison.Ordinal) ||
					err.Message.Contains("404", StringComparison.Ordinal))
				{
					fileMetadata.ResultStatus = ResultStatus.NOT_FOUND_FILE.ToString();
					return new FileHeader();
				}
			}
		}
		else
		{
			// Generate the file URL based on GCS location
			//var url = generateFileURL(fileMetadata.stageInfo.location, fileMetadata.destFileName);
			try
			{
				// Issue a GET response
				m_HttpClient.DefaultRequestHeaders.Add("Authorization", "Bearer ${accessToken}");
				var response = m_HttpClient.Get(fileMetadata.PresignedUrl);

				var digest = response.Headers.GetValues(GCS_METADATA_SFC_DIGEST);
				var contentLength = response.Headers.GetValues("content-length");

				fileMetadata.ResultStatus = ResultStatus.UPLOADED.ToString();

				return new FileHeader
				{
					digest = digest.ToString(),
					contentLength = Convert.ToInt64(contentLength, CultureInfo.InvariantCulture)
				};
			}
			catch (HttpRequestException err)
			{
				// If file doesn't exist, GET request fails
				fileMetadata.LastError = err;
				if (err.Message.Contains("401", StringComparison.Ordinal))
				{
					fileMetadata.ResultStatus = ResultStatus.RENEW_TOKEN.ToString();
				}
				else if (err.Message.Contains("403", StringComparison.Ordinal) ||
					err.Message.Contains("500", StringComparison.Ordinal) ||
					err.Message.Contains("503", StringComparison.Ordinal))
				{
					fileMetadata.ResultStatus = ResultStatus.NEED_RETRY.ToString();
				}
				else if (err.Message.Contains("404", StringComparison.Ordinal))
				{
					fileMetadata.ResultStatus = ResultStatus.NOT_FOUND_FILE.ToString();
				}
				else
				{
					fileMetadata.ResultStatus = ResultStatus.ERROR.ToString();
				}
			}
		}
		return null;
	}

	/// <summary>
	/// Generate the file URL.
	/// </summary>
	/// <param name="stageLocation">The GCS file metadata.</param>
	/// <param name="fileName">The GCS file metadata.</param>
	static string GenerateFileURL(string stageLocation, string fileName)
	{
		var gcsLocation = ExtractBucketNameAndPath(stageLocation);
		var fullFilePath = gcsLocation.Key + fileName;
		var link = "https://storage.googleapis.com/" + gcsLocation.Bucket + "/" + fullFilePath;
		return link;
	}

	/// <summary>
	/// Upload the file to the GCS location.
	/// </summary>
	/// <param name="fileMetadata">The GCS file metadata.</param>
	/// <param name="fileBytes">The file bytes to upload.</param>
	/// <param name="encryptionMetadata">The encryption metadata for the header.</param>
	public void UploadFile(SFFileMetadata fileMetadata, byte[] fileBytes, SFEncryptionMetadata encryptionMetadata)
	{
		// Create the encryption header value
		var encryptionData = JsonConvert.SerializeObject(new EncryptionData
		{
			EncryptionMode = "FullBlob",
			WrappedContentKey = new WrappedContentInfo
			{
				KeyId = "symmKey1",
				EncryptedKey = encryptionMetadata.key,
				Algorithm = "AES_CBC_256"
			},
			EncryptionAgent = new EncryptionAgentInfo
			{
				Protocol = "1.0",
				EncryptionAlgorithm = "AES_CBC_256"
			},
			ContentEncryptionIV = encryptionMetadata.iv,
			KeyWrappingMetadata = new KeyWrappingMetadataInfo
			{
				EncryptionLibrary = "Java 5.3.0"
			}
		});

		// Set the meta header values
		m_HttpClient.DefaultRequestHeaders.Add("x-goog-meta-sfc-digest", fileMetadata.Sha256Digest);
		m_HttpClient.DefaultRequestHeaders.Add("x-goog-meta-matdesc", encryptionMetadata.matDesc);
		m_HttpClient.DefaultRequestHeaders.Add("x-goog-meta-encryptiondata", encryptionData);

		// Convert file bytes to stream
		using var strm = new StreamContent(new MemoryStream(fileBytes));
		// Set the stream content type
		strm.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

		try
		{
			// Issue the POST/PUT request
			var response = m_HttpClient.Put(fileMetadata.PresignedUrl, strm);
		}
		catch (HttpRequestException err)
		{
			fileMetadata.LastError = err;
			if (err.Message.Contains("400", StringComparison.Ordinal))
			{
				fileMetadata.ResultStatus = ResultStatus.RENEW_PRESIGNED_URL.ToString();
			}
			else if (err.Message.Contains("401", StringComparison.Ordinal))
			{
				fileMetadata.ResultStatus = ResultStatus.RENEW_TOKEN.ToString();
			}
			else if (err.Message.Contains("403", StringComparison.Ordinal) ||
				err.Message.Contains("500", StringComparison.Ordinal) ||
				err.Message.Contains("503", StringComparison.Ordinal))
			{
				fileMetadata.ResultStatus = ResultStatus.NEED_RETRY.ToString();
			}
			return;
		}

		fileMetadata.DestFileSize = fileMetadata.UploadSize;
		fileMetadata.ResultStatus = ResultStatus.UPLOADED.ToString();
	}

	/// <summary>
	/// Download the file to the local location.
	/// </summary>
	/// <param name="fileMetadata">The GCS file metadata.</param>
	/// <param name="fullDstPath">The local location to store downloaded file into.</param>
	/// <param name="maxConcurrency">Number of max concurrency.</param>
	public void DownloadFile(SFFileMetadata fileMetadata, string fullDstPath, int maxConcurrency)
	{
		try
		{
			// Issue the POST/PUT request
			var response = m_HttpClient.Get(fileMetadata.PresignedUrl);

			// Write to file
			using (var fileStream = File.Create(fullDstPath))
			{
				var responseTask = response.Content.ReadAsStreamAsync();
				responseTask.Wait();

				responseTask.Result.CopyTo(fileStream);
			}

			var headers = response.Headers;

			// Get header values
			dynamic? encryptionData = null;
			if (headers.TryGetValues(GCS_METADATA_ENCRYPTIONDATAPROP, out var values1))
			{
				encryptionData = JsonConvert.DeserializeObject(values1.First());
			}

			string? matDesc = null;
			if (headers.TryGetValues(GCS_METADATA_MATDESC_KEY, out var values2))
			{
				matDesc = values2.First();
			}

			// Get encryption metadata from encryption data header value
			SFEncryptionMetadata? encryptionMetadata = null;
			if (encryptionData != null)
			{
				encryptionMetadata = new SFEncryptionMetadata
				{
					iv = encryptionData["ContentEncryptionIV"],
					key = encryptionData["WrappedContentKey"]["EncryptedKey"],
					matDesc = matDesc
				};
				fileMetadata.EncryptionMetadata = encryptionMetadata;
			}

			if (headers.TryGetValues(GCS_METADATA_SFC_DIGEST, out var values3))
			{
				fileMetadata.Sha256Digest = values3.First();
			}

			if (headers.TryGetValues(GCS_FILE_HEADER_CONTENT_LENGTH, out var values4))
			{
				fileMetadata.SrcFileSize = (long)Convert.ToDouble(values4.First(), CultureInfo.InvariantCulture);
			}
		}
		catch (HttpRequestException err)
		{
			fileMetadata.LastError = err;
			if (err.Message.Contains("401", StringComparison.Ordinal))
			{
				fileMetadata.ResultStatus = ResultStatus.RENEW_TOKEN.ToString();
			}
			else if (err.Message.Contains("403", StringComparison.Ordinal) ||
				err.Message.Contains("500", StringComparison.Ordinal) ||
				err.Message.Contains("503", StringComparison.Ordinal))
			{
				fileMetadata.ResultStatus = ResultStatus.NEED_RETRY.ToString();
			}
			return;
		}

		fileMetadata.ResultStatus = ResultStatus.DOWNLOADED.ToString();
	}

	public void Dispose()
	{
		m_StorageClient.Dispose();
		m_HttpClient.Dispose();
	}
}
