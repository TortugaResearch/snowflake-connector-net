﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using Tortuga.Data.Snowflake.Core.Messages;

namespace Tortuga.Data.Snowflake.Core.FileTransfer.StorageClient;

/// <summary>
/// The interface for the storage clients.
/// </summary>
class SFRemoteStorage : SFStorage
{
	public static readonly SFRemoteStorage Instance = new();

	private SFRemoteStorage()
	{
	}

	/// <summary>
	/// Strings to indicate specific storage type.
	/// </summary>
	const string S3_FS = "S3";

	const string AZURE_FS = "AZURE";
	const string GCS_FS = "GCS";
	const string LOCAL_FS = "LOCAL_FS";

	/// <summary>
	/// Amount of concurrency to use by default.
	/// </summary>
	const int DEFAULT_CONCURRENCY = 1;

	/// <summary>
	/// Maximum amount of times to retry.
	/// </summary>
	const int DEFAULT_MAX_RETRY = 5;

	/// <summary>
	/// Instantiate a new storage client.
	/// </summary>
	/// <param name="stageInfo">The stage info used to create the client.</param>
	/// <returns>A new instance of the storage client.</returns>
	internal static ISFRemoteStorageClient? GetRemoteStorageType(PutGetResponseData response)
	{
		var stageInfo = response.StageInfo;
		var stageLocationType = stageInfo!.LocationType;

		// Create the storage type based on location type
		if (stageLocationType == LOCAL_FS)
		{
			throw new NotImplementedException();
		}
		else if (stageLocationType == S3_FS)
		{
			return new SFS3Client(stageInfo, DEFAULT_MAX_RETRY, response.Parallel);
		}
		else if (stageLocationType == AZURE_FS)
		{
			return new SFSnowflakeAzureClient(stageInfo);
		}
		else if (stageLocationType == GCS_FS)
		{
			return new SFGCSClient(stageInfo);
		}
		else
		{
			return null;
		}
	}

	/// <summary>
	/// Encrypt then upload one file.
	/// </summary>
	/// <summary>
	/// <param name="fileMetadata">The file metadata of the file to upload</param>
	internal static void UploadOneFile(SFFileMetadata fileMetadata)
	{
		if (fileMetadata.RealSrcFilePath == null)
			throw new ArgumentException("fileMetadata.realSrcFilePath is null", nameof(fileMetadata));
		if (fileMetadata.Client == null)
			throw new ArgumentException("fileMetadata.client is null", nameof(fileMetadata));

		var encryptionMetadata = new SFEncryptionMetadata();
		var fileBytes = File.ReadAllBytes(fileMetadata.RealSrcFilePath);

		// If encryption enabled, encrypt the file to be uploaded
		if (fileMetadata.EncryptionMaterial != null)
		{
			fileBytes = EncryptionProvider.EncryptFile(
				fileMetadata.RealSrcFilePath,
				fileMetadata.EncryptionMaterial,
				encryptionMetadata);
		}

		var maxRetry = DEFAULT_MAX_RETRY;
		Exception? lastErr = null;

		// Attempt to upload and retry if fails
		for (var retry = 0; retry < maxRetry; retry++)
		{
			var client = fileMetadata.Client;

			if (!fileMetadata.Overwrite)
			{
				// Get the file metadata
				var fileHeader = client.GetFileHeader(fileMetadata);
				if (fileHeader != null &&
					fileMetadata.ResultStatus == ResultStatus.UPLOADED.ToString())
				{
					// File already exists
					fileMetadata.DestFileSize = 0;
					fileMetadata.ResultStatus = ResultStatus.SKIPPED.ToString();
					return;
				}
			}

			if (fileMetadata.Overwrite || fileMetadata.ResultStatus == ResultStatus.NOT_FOUND_FILE.ToString())
			{
				// Upload the file
				client.UploadFile(fileMetadata, fileBytes, encryptionMetadata);
			}

			if (fileMetadata.ResultStatus == ResultStatus.UPLOADED.ToString() ||
				fileMetadata.ResultStatus == ResultStatus.RENEW_TOKEN.ToString() ||
				fileMetadata.ResultStatus == ResultStatus.RENEW_PRESIGNED_URL.ToString())
			{
				return;
			}
			else if (fileMetadata.ResultStatus == ResultStatus.NEED_RETRY_WITH_LOWER_CONCURRENCY.ToString())
			{
				lastErr = fileMetadata.LastError;

				var maxConcurrency = fileMetadata.Parallel - Convert.ToInt32(retry * fileMetadata.Parallel / maxRetry);
				maxConcurrency = Math.Max(DEFAULT_CONCURRENCY, maxConcurrency);
				fileMetadata.LastMaxConcurrency = maxConcurrency;

				// Failed to upload file, retrying
				var sleepingTime = Math.Min(Math.Pow(2, retry), 16);
				Thread.Sleep((int)sleepingTime);
			}
			else if (fileMetadata.ResultStatus == ResultStatus.NEED_RETRY.ToString())
			{
				lastErr = fileMetadata.LastError;

				// Failed to upload file, retrying
				var sleepingTime = Math.Min(Math.Pow(2, retry), 16);
				Thread.Sleep((int)sleepingTime);
			}
		}
		if (lastErr != null)
		{
			throw lastErr;
		}
		else
		{
			var msg = "Unknown Error in uploading a file: " + fileMetadata.DestFileName;
			throw new SnowflakeDbException(SnowflakeDbError.InternalError, msg);
		}
	}

	/// <summary>
	/// Attempt upload of a file and retry if fails.
	/// </summary>
	/// <param name="fileMetadata">The file metadata of the file to upload</param>
	internal override void UploadOneFileWithRetry(SFFileMetadata fileMetadata)
	{
		if (fileMetadata.Client == null)
			throw new ArgumentException("fileMetadata.client is null", nameof(fileMetadata));

		var breakFlag = false;

		for (var count = 0; count < 10; count++)
		{
			// Upload the file
			UploadOneFile(fileMetadata);
			if (fileMetadata.ResultStatus == ResultStatus.UPLOADED.ToString())
			{
				for (var count2 = 0; count2 < 10; count2++)
				{
					// Get the file metadata
					fileMetadata.Client.GetFileHeader(fileMetadata);
					// Check result status if file already exists
					if (fileMetadata.ResultStatus == ResultStatus.NOT_FOUND_FILE.ToString())
					{
						// Wait 1 second
						Thread.Sleep(1000);
						continue;
					}
					break;
				}
			}
			// Break out of loop if file is successfully uploaded or already exists
			if (fileMetadata.ResultStatus == ResultStatus.UPLOADED.ToString() ||
				fileMetadata.ResultStatus == ResultStatus.SKIPPED.ToString())
			{
				breakFlag = true;
				break;
			}
		}
		if (!breakFlag)
		{
			// Could not upload a file even after retry
			fileMetadata.ResultStatus = ResultStatus.ERROR.ToString();
		}
		return;
	}

	/// <summary>
	/// Download one file.
	/// </summary>
	/// <summary>
	/// <param name="fileMetadata">The file metadata of the file to download</param>
	internal override void DownloadOneFile(SFFileMetadata fileMetadata)
	{
		if (fileMetadata.LocalLocation == null)
			throw new ArgumentException("fileMetadata.localLocation is null", nameof(fileMetadata));
		if (fileMetadata.DestFileName == null)
			throw new ArgumentException("fileMetadata.destFileName is null", nameof(fileMetadata));
		if (fileMetadata.Client == null)
			throw new ArgumentException("fileMetadata.client is null", nameof(fileMetadata));

		var fullDstPath = Path.Combine(fileMetadata.LocalLocation, fileMetadata.DestFileName);

		// Check local location exists
		Directory.CreateDirectory(fileMetadata.LocalLocation);

		var client = fileMetadata.Client;
		var fileHeader = client.GetFileHeader(fileMetadata);

		if (fileHeader != null)
		{
			fileMetadata.SrcFileSize = fileHeader.contentLength;
		}

		var maxConcurrency = fileMetadata.Parallel;
		Exception? lastErr = null;
		var maxRetry = DEFAULT_MAX_RETRY;

		for (var retry = 0; retry < maxRetry; retry++)
		{
			// Download the file
			client.DownloadFile(fileMetadata, fullDstPath, maxConcurrency);

			if (fileMetadata.ResultStatus == ResultStatus.DOWNLOADED.ToString())
			{
				if (fileMetadata.EncryptionMaterial != null)
				{
					/**
					  * For storage utils that do not have the privilege of
					  * getting the metadata early, both object and metadata
					  * are downloaded at once.In which case, the file meta will
					  * be updated with all the metadata that we need and
					  * then we can call getFileHeader to get just that and also
					  * preserve the idea of getting metadata in the first place.
					  * One example of this is the utils that use pre-signed url
					  * for upload / download and not the storage client library.
					  **/
					if (fileMetadata.PresignedUrl != null)
					{
						fileHeader = client.GetFileHeader(fileMetadata);
					}

					var tmpDstName = EncryptionProvider.DecryptFile(
					  fullDstPath,
					  fileMetadata.EncryptionMaterial,
					  fileHeader!.encryptionMetadata!  //If encryptionMaterial is not null, then we must have seen a file header.
					  );

					File.Delete(fullDstPath);

					// Copy decrypted tmp file to target destination path
					File.Copy(tmpDstName, fullDstPath);

					// Delete tmp file
					File.Delete(tmpDstName);
				}

				var fileInfo = new FileInfo(fullDstPath);
				fileMetadata.DestFileSize = fileInfo.Length;
				return;
			}
			else if (fileMetadata.ResultStatus == ResultStatus.RENEW_TOKEN.ToString() ||
				fileMetadata.ResultStatus == ResultStatus.RENEW_PRESIGNED_URL.ToString())
			{
				return;
			}
			else if (fileMetadata.ResultStatus == ResultStatus.NEED_RETRY_WITH_LOWER_CONCURRENCY.ToString())
			{
				lastErr = fileMetadata.LastError;
				// Failed to download file, retrying with max concurrency
				maxConcurrency = fileMetadata.Parallel - (retry * fileMetadata.Parallel / maxRetry);
				maxConcurrency = Math.Max(DEFAULT_CONCURRENCY, maxConcurrency);
				fileMetadata.LastMaxConcurrency = maxConcurrency;

				var sleepingTime = Convert.ToInt32(Math.Min(Math.Pow(2, retry), 16));
				Thread.Sleep(sleepingTime);
			}
			else if (fileMetadata.ResultStatus == ResultStatus.NEED_RETRY.ToString())
			{
				lastErr = fileMetadata.LastError;

				var sleepingTime = Convert.ToInt32(Math.Min(Math.Pow(2, retry), 16));
				Thread.Sleep(sleepingTime);
			}
		}
		if (lastErr != null)
		{
			throw lastErr;
		}
		else
		{
			var msg = "Unknown Error in downloading a file: " + fileMetadata.DestFileName;
			throw new SnowflakeDbException(SnowflakeDbError.InternalError, msg);
		}
	}
}
