﻿/*
 * Copyright (c) 2021 Snowflake Computing Inc. All rights reserved.
 */

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tortuga.Data.Snowflake.Core.FileTransfer.StorageClient;
using Tortuga.Data.Snowflake.Core.Messages;
using Tortuga.Data.Snowflake.Core.RequestProcessing;
using Tortuga.Data.Snowflake.Core.ResponseProcessing;
using Tortuga.Data.Snowflake.Core.Sessions;

namespace Tortuga.Data.Snowflake.Core.FileTransfer;

/// <summary>
/// Class responsible for uploading and downloading files to the remote client.
/// </summary>
class SFFileTransferAgent
{
	/// <summary>
	/// Auto-detect keyword for source compression type auto detection.
	/// </summary>
	static readonly string COMPRESSION_AUTO_DETECT = "auto_detect";

	/// <summary>
	/// The Snowflake query
	/// </summary>
	string Query;

	/// <summary>
	/// The Snowflake session
	/// </summary>
	SFSession Session;

	/// <summary>
	/// External cancellation token, used to stop the transfer
	/// </summary>
	CancellationToken externalCancellationToken;

	/// <summary>
	/// The type of transfer either UPLOAD or DOWNLOAD.
	/// </summary>
	readonly CommandTypes CommandType;

	/// <summary>
	/// The file metadata. Applies to all files being uploaded/downloaded
	/// </summary>
	readonly PutGetResponseData TransferMetadata;

	/// <summary>
	/// List of metadata for small and large files.
	/// </summary>
	List<SFFileMetadata> FilesMetas = new();

	List<SFFileMetadata> SmallFilesMetas = new();
	List<SFFileMetadata> LargeFilesMetas = new();

	/// <summary>
	/// List of metadata for the resulting file after upload/download.
	/// </summary>
	List<SFFileMetadata> ResultsMetas = new();

	/// <summary>
	/// List of encryption materials of the files to be uploaded/downloaded.
	/// </summary>
	List<PutGetEncryptionMaterial> EncryptionMaterials = new();

	/// <summary>
	/// Time to wait before uploading the next file.
	/// </summary>
	int INJECT_WAIT_IN_PUT = 0;

	/// <summary>
	/// String indicating local storage type.
	/// </summary>
	const string LOCAL_FS = "LOCAL_FS";

	/// <summary>
	/// Constructor.
	/// </summary>
	public SFFileTransferAgent(
		string query,
		SFSession session,
		PutGetResponseData responseData,
		CancellationToken cancellationToken)
	{
		Query = query;
		Session = session;
		TransferMetadata = responseData;
		CommandType = (CommandTypes)Enum.Parse(typeof(CommandTypes), TransferMetadata.command!, true);
		externalCancellationToken = cancellationToken;
	}

	/// <summary>
	/// Execute the PUT/GET command.
	/// </summary>
	public void execute()
	{
		// Initialize the encryption metadata
		initEncryptionMaterial();

		if (CommandTypes.UPLOAD == CommandType)
		{
			// Initialize the list of actual files to upload
			var expandedSrcLocations = new List<string>();
			foreach (var location in TransferMetadata.src_locations!)
			{
				expandedSrcLocations.AddRange(expandFileNames(location));
			}

			// Initialize each file specific metadata (for example, file path, name and size) and
			// put it in1 of the 2 lists : Small files and large files based on a threshold
			// extracted from the command response
			initFileMetadata(expandedSrcLocations);

			if (expandedSrcLocations.Count == 0)
			{
				throw new ArgumentException("No file found for: " + TransferMetadata.src_locations[0].ToString());
			}
		}
		else if (CommandTypes.DOWNLOAD == CommandType)
		{
			initFileMetadata(TransferMetadata.src_locations!);

			Directory.CreateDirectory(TransferMetadata.localLocation!);
		}

		// Update the file metadata with GCS presigned URL
		updatePresignedUrl();

		foreach (var fileMetadata in FilesMetas)
		{
			// If the file is larger than the threshold, add it to the large files list
			// Otherwise add it to the small files list
			if (fileMetadata.srcFileSize > TransferMetadata.threshold)
				LargeFilesMetas.Add(fileMetadata);
			else
				SmallFilesMetas.Add(fileMetadata);
		}

		// Check command type
		if (CommandTypes.UPLOAD == CommandType)
			upload();
		else if (CommandTypes.DOWNLOAD == CommandType)
			download();
	}

	/// <summary>
	/// Generate the result set based on the file metadata.
	/// </summary>
	/// <returns>The result set containing file status and info</returns>
	public SFBaseResultSet result()
	{
		// Set the row count using the number of metadata in the result metas
		TransferMetadata.rowSet = new string[ResultsMetas.Count, 8];

		// For each file metadata, set the result set variables
		for (int index = 0; index < ResultsMetas.Count; index++)
		{
			TransferMetadata.rowSet[index, 0] = ResultsMetas[index].srcFileName;
			TransferMetadata.rowSet[index, 1] = ResultsMetas[index].destFileName;
			TransferMetadata.rowSet[index, 2] = ResultsMetas[index].srcFileSize.ToString();
			TransferMetadata.rowSet[index, 3] = ResultsMetas[index].destFileSize.ToString();
			TransferMetadata.rowSet[index, 4] = ResultsMetas[index].resultStatus;

			if (ResultsMetas[index].lastError != null)
				TransferMetadata.rowSet[index, 5] = ResultsMetas[index].lastError!.ToString();
			else
				TransferMetadata.rowSet[index, 5] = null;

			if (ResultsMetas[index].sourceCompression.Name != null)
				TransferMetadata.rowSet[index, 6] = ResultsMetas[index].sourceCompression.Name;
			else
				TransferMetadata.rowSet[index, 6] = null;

			if (ResultsMetas[index].targetCompression.Name != null)
				TransferMetadata.rowSet[index, 7] = ResultsMetas[index].targetCompression.Name;
			else
				TransferMetadata.rowSet[index, 7] = null;
		}

		return new SFResultSet(TransferMetadata, new SFStatement(Session), externalCancellationToken);
	}

	/// <summary>
	/// Upload files sequentially or in parallel.
	/// </summary>
	void upload()
	{
		//Start the upload tasks(for small files upload in parallel using the given parallelism
		//factor, for large file updload sequentially)
		//For each file, using the remote client
		if (0 < LargeFilesMetas.Count)
		{
			foreach (SFFileMetadata fileMetadata in LargeFilesMetas)
				UploadFilesInSequential(fileMetadata);
		}

		if (0 < SmallFilesMetas.Count)
			UploadFilesInParallel(SmallFilesMetas, TransferMetadata.parallel);
	}

	/// <summary>
	/// Download files sequentially or in parallel.
	/// </summary>
	void download()
	{
		//Start the download tasks(for small files download in parallel using the given parallelism
		//factor, for large file download sequentially)
		//For each file, using the remote client
		if (0 < LargeFilesMetas.Count)
		{
			foreach (SFFileMetadata fileMetadata in LargeFilesMetas)
				DownloadFilesInSequential(fileMetadata);
		}
		if (0 < SmallFilesMetas.Count)
			DownloadFilesInParallel(SmallFilesMetas, TransferMetadata.parallel);
	}

	/// <summary>
	/// Get the presigned URL and update the file metadata.
	/// </summary>
	void updatePresignedUrl()
	{
		// Presigned url only applies to GCS
		if (TransferMetadata.stageInfo!.locationType == "GCS")
		{
			if (CommandTypes.UPLOAD == CommandType)
			{
				foreach (var fileMeta in FilesMetas)
				{
					var filePathToReplace = getFilePathFromPutCommand(Query);
					var fileNameToReplaceWith = fileMeta.destFileName;
					var queryWithSingleFile = Query;
					queryWithSingleFile = queryWithSingleFile.Replace(filePathToReplace, fileNameToReplaceWith);

					var sfStatement = new SFStatement(Session);
					sfStatement.m_IsPutGetQuery = true;

					var response = sfStatement.ExecuteHelper<PutGetExecResponse, PutGetResponseData>(0, queryWithSingleFile, null, false);

					fileMeta.stageInfo = response.data!.stageInfo!;
					fileMeta.presignedUrl = response.data!.stageInfo!.presignedUrl;
				}
			}
			else if (CommandTypes.DOWNLOAD == CommandType)
			{
				for (var index = 0; index < FilesMetas.Count; index++)
					FilesMetas[index].presignedUrl = TransferMetadata.presignedUrls![index];
			}
		}
	}

	/// <summary>
	/// Obtain the file path from the PUT query.
	/// </summary>
	/// <param name="query">The query containing the file path</param>
	/// <returns>The file path contained by the query</returns>
	string getFilePathFromPutCommand(string query)
	{
		// Extract file path from PUT command:
		// E.g. "PUT file://C:<path-to-file> @DB.SCHEMA.%TABLE;"
		var startIndex = query.IndexOf("file://") + "file://".Length;
		var endIndex = query.Substring(startIndex).IndexOf(' ');
		var filePath = query.Substring(startIndex, endIndex);
		return filePath;
	}

	/// <summary>
	/// Initialize the encryption materials for file encryption.
	/// </summary>
	void initEncryptionMaterial()
	{
		if (CommandTypes.UPLOAD == CommandType)
			EncryptionMaterials.Add(TransferMetadata.encryptionMaterial![0]);
	}

	/// <summary>
	/// Initialize the file metadata of each file to be uploaded/downloaded.
	/// </summary>
	/// <param name="files">List of files to obtain metadata from</param>
	void initFileMetadata(List<string> files)
	{
		if (CommandTypes.UPLOAD == CommandType)
		{
			foreach (var file in files)
			{
				var fileInfo = new FileInfo(file);

				//  Retrieve / Compute the file actual compression type for each file in the list(most work is for auto - detect)
				var fileName = fileInfo.Name;
				SFFileCompressionTypes.SFFileCompressionType compressionType;

				if (TransferMetadata.autoCompress && TransferMetadata.sourceCompression!.Equals(COMPRESSION_AUTO_DETECT))
				{
					// Auto-detect source compression type
					// Will return NONE if no matching type is found
					compressionType = SFFileCompressionTypes.GuessCompressionType(file);
				}
				else
				{
					// User defined source compression type
					compressionType = SFFileCompressionTypes.LookUpByName(TransferMetadata.sourceCompression!);
				}

				// Verify that the compression type is supported
				if (!compressionType.IsSupported)
				{
					//   SqlState.FEATURE_NOT_SUPPORTED = 0A000
					throw new SnowflakeDbException("0A000", SFError.INTERNAL_ERROR, compressionType.Name);
				}

				var fileMetadata = new SFFileMetadata()
				{
					srcFilePath = file,
					srcFileName = fileName,
					srcFileSize = fileInfo.Length,
					stageInfo = TransferMetadata.stageInfo,
					overwrite = TransferMetadata.overwrite,
					// Need to compress before sending only if autoCompress is On and the file is
					// not compressed yet
					requireCompress = (TransferMetadata.autoCompress && (SFFileCompressionTypes.NONE.Equals(compressionType))),
					sourceCompression = compressionType,
					presignedUrl = TransferMetadata.stageInfo!.presignedUrl,
					// If the file is under the threshold, don't upload in chunks, set parallel to 1
					parallel = (fileInfo.Length > TransferMetadata.threshold) ? TransferMetadata.parallel : 1,
				};

				if (!fileMetadata.requireCompress)
				{
					// The file is already compressed
					fileMetadata.targetCompression = fileMetadata.sourceCompression;
					fileMetadata.destFileName = fileName;
				}
				else
				{
					// The file will need to be compressed using gzip
					fileMetadata.targetCompression = SFFileCompressionTypes.GZIP;
					fileMetadata.destFileName = fileName + SFFileCompressionTypes.GZIP.FileExtension;
				}

				if (EncryptionMaterials.Count > 0)
					fileMetadata.encryptionMaterial = EncryptionMaterials[0];

				FilesMetas.Add(fileMetadata);
			}
		}
		else if (CommandTypes.DOWNLOAD == CommandType)
		{
			for (var index = 0; index < files.Count; index++)
			{
				var file = files[index];
				var fileMetadata = new SFFileMetadata()
				{
					srcFileName = file,
					destFileName = file,
					localLocation = TransferMetadata.localLocation,
					stageInfo = TransferMetadata.stageInfo,
					overwrite = TransferMetadata.overwrite,
					presignedUrl = TransferMetadata.stageInfo!.presignedUrl,
					parallel = TransferMetadata.parallel,
					encryptionMaterial = TransferMetadata.encryptionMaterial![index]
				};

				FilesMetas.Add(fileMetadata);
			}
		}
	}

	/// <summary>
	/// Expand the expand the wildcards if any to generate the list of paths for all files
	/// matched by the wildcards. Also replace
	/// Get the absolute path for the file.
	/// </summary>
	/// <param name="location">The path to expand</param>
	/// <returns>The list of file matching the input location</returns>
	/// <exception cref="DirectoryNotFoundException">Directory not found. Could not find a part of the pat </exception>
	/// <exception cref="FileNotFoundException">File not found or the path is pointing to a Directory</exception>
	List<string> expandFileNames(string location)
	{
		// Replace ~ with the user home directory path
		if (location.Contains("~"))
		{
			var homePath = (Environment.OSVersion.Platform == PlatformID.Unix ||
			Environment.OSVersion.Platform == PlatformID.MacOSX)
			? Environment.GetEnvironmentVariable("HOME")
			: Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%");

			location = location.Replace("~", homePath);
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			location = Path.GetFullPath(location);

		var fileName = Path.GetFileName(location);
		var directoryName = Path.GetDirectoryName(location)!;

		var filePaths = new List<string>();
		//filePaths.Add(""); //Start with an empty string to build upon
		if (directoryName.Contains("?") || directoryName.Contains("*"))
		{
			// If there is a wildcard in at least one of the directory name in the file path
			var pathParts = location.Split(Path.DirectorySeparatorChar);

			string currPart;
			for (var i = 0; i < pathParts.Length; i++)
			{
				var tempPaths = new List<string>();
				foreach (var filePath in filePaths)
				{
					currPart = pathParts[i];

					if (currPart.Contains("?") || currPart.Contains("*"))
					{
						if (i < pathParts.Length - 1)
						{
							// Expand the directories names
							tempPaths.AddRange(Directory.GetDirectories(filePath, currPart, SearchOption.TopDirectoryOnly));
						}
						else
						{
							// Expand the files names
							tempPaths.AddRange(Directory.GetFiles(filePath, currPart, SearchOption.TopDirectoryOnly));
						}
					}
					else
					{
						if (0 < i) // Keep building the paths
							tempPaths.Add(filePath + Path.DirectorySeparatorChar + currPart);
						else // First part
							tempPaths.Add(currPart);
					}
				}
				filePaths = tempPaths;
			}
		}
		else if (fileName.Contains("?") || fileName.Contains("*"))
		{
			var ext = Path.GetExtension(fileName);
			if ((4 == ext.Length) && (fileName.Contains("*")))
			{
				/*
					* When you use the asterisk wildcard character in a searchPattern such as
					* "*.txt", the number of characters in the specified extension affects the
					* search as follows:
					* - If the specified extension is exactly three characters long, the method
					* returns files with extensions that begin with the specified extension.
					* For example, "*.xls" returns both "book.xls" and "book.xlsx".
					* - In all other cases, the method returns files that exactly match the
					* specified extension. For example, "*.ai" returns "file.ai" but not "file.aif".
					*/
				var potentialMatches = Directory.GetFiles(directoryName, fileName, SearchOption.TopDirectoryOnly);
				foreach (var potentialMatch in potentialMatches)
				{
					if (potentialMatch.EndsWith(ext))
						filePaths.Add(potentialMatch);
				}
			}
			else
			{
				// If there is a wildcard in the file name in the file path
				filePaths.AddRange(
					Directory.GetFiles(
						directoryName,
						fileName,
						SearchOption.TopDirectoryOnly));
			}
		}
		else
		{
			// No wild card, just make sure it's a file
			var attr = File.GetAttributes(location);
			if (!attr.HasFlag(FileAttributes.Directory))
				filePaths.Add(location);
			else
				throw new FileNotFoundException("Directories not supported, you need to provide a file path", location);
		}

		return filePaths;
	}

	/// <summary>
	/// Compress a file using the given file metadata (file path, compression type, etc...) and
	/// update the metadata accordingly after the compression is finished.
	/// </summary>
	/// <param name="fileMetadata">The metadata for the file to compress.</param>
	void compressFileWithGzip(SFFileMetadata fileMetadata)
	{
		if (fileMetadata.srcFilePath == null)
			throw new ArgumentException("fileMetadata.srcFilePath is null", nameof(fileMetadata));
		if (fileMetadata.tmpDir == null)
			throw new ArgumentException("fileMetadata.tmpDir is null", nameof(fileMetadata));

		var fileToCompress = new FileInfo(fileMetadata.srcFilePath);
		fileMetadata.realSrcFilePath = Path.Combine(fileMetadata.tmpDir, fileMetadata.srcFileName + "_c.gz");

		using (var originalFileStream = fileToCompress.OpenRead())
		{
			if ((File.GetAttributes(fileToCompress.FullName) &
			   FileAttributes.Hidden) != FileAttributes.Hidden)
			{
				using (var compressedFileStream = File.Create(fileMetadata.realSrcFilePath))
				{
					using (var compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress))
					{
						originalFileStream.CopyTo(compressionStream);
					}
				}

				FileInfo destInfo = new FileInfo(fileMetadata.realSrcFilePath);
				fileMetadata.destFileSize = destInfo.Length;
			}
		}
	}

	/// <summary>
	/// Get digest and size of file to be uploaded.
	/// </summary>
	/// <param name="fileMetadata">The metadata for the file to get digest.</param>
	void getDigestAndSizeForFile(SFFileMetadata fileMetadata)
	{
		if (fileMetadata.realSrcFilePath == null)
			throw new ArgumentException("fileMetadata.realSrcFilePath is null", nameof(fileMetadata));

		using (SHA256 SHA256 = SHA256.Create())
		{
			using (FileStream fileStream = File.OpenRead(fileMetadata.realSrcFilePath))
			{
				fileMetadata.sha256Digest = Convert.ToBase64String(SHA256.ComputeHash(fileStream));
				fileMetadata.uploadSize = fileStream.Length;
			}
		}
	}

	/// <summary>
	/// Renew expired client.
	/// </summary>
	/// <returns>The renewed storage client.</returns>
	ISFRemoteStorageClient? renewExpiredClient()
	{
		var sfStatement = new SFStatement(Session);

		var response = sfStatement.ExecuteHelper<PutGetExecResponse, PutGetResponseData>(0, TransferMetadata.command!, null, false);

		return SFRemoteStorageUtil.GetRemoteStorageType(response.data!);
	}

	/// <summary>
	/// Upload a list of files in parallel using the given parallelization factor.
	/// </summary>
	/// <param name="fileMetadata">The metadata of the file to upload.</param>
	/// <returns>The result outcome for each file.</returns>
	void UploadFilesInSequential(
		SFFileMetadata fileMetadata)
	{
		/// The storage client used to upload/download data from files or streams
		fileMetadata.client = SFRemoteStorageUtil.GetRemoteStorageType(TransferMetadata);
		var resultMetadata = UploadSingleFile(fileMetadata);

		if (resultMetadata.resultStatus == ResultStatus.RENEW_TOKEN.ToString())
			fileMetadata.client = renewExpiredClient();
		else if (resultMetadata.resultStatus == ResultStatus.RENEW_PRESIGNED_URL.ToString())
			updatePresignedUrl();

		ResultsMetas.Add(resultMetadata);

		if (INJECT_WAIT_IN_PUT > 0)
			Thread.Sleep(INJECT_WAIT_IN_PUT);
	}

	/// <summary>
	/// Download a list of files in parallel using the given parallelization factor.
	/// </summary>
	/// <param name="fileMetadata">The metadata of the file to download.</param>
	/// <returns>The result outcome for each file.</returns>
	void DownloadFilesInSequential(
		SFFileMetadata fileMetadata)
	{
		/// The storage client used to upload/download data from files or streams
		fileMetadata.client = SFRemoteStorageUtil.GetRemoteStorageType(TransferMetadata);
		var resultMetadata = DownloadSingleFile(fileMetadata);

		if (resultMetadata.resultStatus == ResultStatus.RENEW_TOKEN.ToString())
			fileMetadata.client = renewExpiredClient();
		else if (resultMetadata.resultStatus == ResultStatus.RENEW_PRESIGNED_URL.ToString())
			updatePresignedUrl();

		ResultsMetas.Add(resultMetadata);

		if (INJECT_WAIT_IN_PUT > 0)
			Thread.Sleep(INJECT_WAIT_IN_PUT);
	}

	/// <summary>
	/// Upload a list of files in parallel using the given parallelization factor.
	/// </summary>
	/// <param name="filesMetadata">The list of files to upload in parallel.</param>
	/// <param name="parallel">The number of files to upload in parallel.</param>
	/// <returns>The result outcome for each file.</returns>
	void UploadFilesInParallel(
		List<SFFileMetadata> filesMetadata,
		int parallel)
	{
		var listOfActions = new List<Action>();
		foreach (var fileMetadata in filesMetadata)
			listOfActions.Add(() => UploadFilesInSequential(fileMetadata));

		var options = new ParallelOptions { MaxDegreeOfParallelism = parallel };
		Parallel.Invoke(options, listOfActions.ToArray());
	}

	/// <summary>
	/// Download a list of files in parallel using the given parallelization factor.
	/// </summary>
	/// <param name="filesMetadata">The list of files to download in parallel.</param>
	/// <param name="parallel">The number of files to download in parallel.</param>
	/// <returns>The result outcome for each file.</returns>
	void DownloadFilesInParallel(
		List<SFFileMetadata> filesMetadata,
		int parallel)
	{
		var listOfActions = new List<Action>();
		foreach (SFFileMetadata fileMetadata in filesMetadata)
			listOfActions.Add(() => DownloadFilesInSequential(fileMetadata));

		var options = new ParallelOptions { MaxDegreeOfParallelism = parallel };
		Parallel.Invoke(options, listOfActions.ToArray());
	}

	/// <summary>
	/// Upload a single file.
	/// </summary>
	/// <param name="storageClient">Storage client to upload the file with.</param>
	/// <param name="fileMetadata">The metadata of the file to upload.</param>
	/// <returns>The result outcome.</returns>
	SFFileMetadata UploadSingleFile(
		SFFileMetadata fileMetadata)
	{
		fileMetadata.realSrcFilePath = fileMetadata.srcFilePath;

		// Create tmp folder to store compressed files
		fileMetadata.tmpDir = GetTemporaryDirectory();

		try
		{
			// Compress the file if needed
			if (fileMetadata.requireCompress)
				compressFileWithGzip(fileMetadata);

			// Calculate the digest
			getDigestAndSizeForFile(fileMetadata);

			if (StorageClientType.REMOTE == GetStorageClientType(TransferMetadata.stageInfo!))
				// Upload the file using the remote client SDK and the file metadata
				SFRemoteStorageUtil.UploadOneFileWithRetry(fileMetadata);
			else
				// Upload the file using the local client SDK and the file metadata
				SFLocalStorageUtil.UploadOneFileWithRetry(fileMetadata);
		}
		finally
		{
			Directory.Delete(fileMetadata.tmpDir, true);
		}

		return fileMetadata;
	}

	/// <summary>
	/// Download a single file.
	/// </summary>
	/// <param name="storageClient">Storage client to download the file with.</param>
	/// <param name="fileMetadata">The metadata of the file to download.</param>
	/// <returns>The result outcome.</returns>
	SFFileMetadata DownloadSingleFile(
		SFFileMetadata fileMetadata)
	{
		// Create tmp folder to store compressed files
		fileMetadata.tmpDir = GetTemporaryDirectory();

		try
		{
			if (StorageClientType.REMOTE == GetStorageClientType(TransferMetadata.stageInfo!))
				// Upload the file using the remote client SDK and the file metadata
				SFRemoteStorageUtil.DownloadOneFile(fileMetadata);
			else
				// Upload the file using the local client SDK and the file metadata
				SFLocalStorageUtil.DownloadOneFile(fileMetadata);
		}
		finally
		{
			Directory.Delete(fileMetadata.tmpDir, true);
		}

		return fileMetadata;
	}

	/// <summary>
	/// Create a temporary directory.
	/// </summary>
	/// <returns>The temporary directory name.</returns>
	/// Referenced from: https://stackoverflow.com/a/278457
	public string GetTemporaryDirectory()
	{
		var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
		Directory.CreateDirectory(tempDirectory);
		return tempDirectory;
	}

	/// <summary>
	/// Get the storage client type.
	/// </summary>
	/// <param name="stageInfo">The stage info used to get the stage location type.</param>
	/// <returns>The storage client type.</returns>
	public StorageClientType GetStorageClientType(PutGetStageInfo stageInfo)
	{
		var stageLocationType = stageInfo.locationType;

		if (stageLocationType == LOCAL_FS)
			return StorageClientType.LOCAL;
		else
			return StorageClientType.REMOTE;
	}
}
