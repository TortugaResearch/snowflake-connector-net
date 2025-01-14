﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using System.Globalization;
using Tortuga.Data.Snowflake.Core.RequestProcessing;
using Tortuga.Data.Snowflake.Core.Sessions;

namespace Tortuga.Data.Snowflake.Core.ResponseProcessing.Chunks;

class SFBlockingChunkDownloaderV3 : IChunkDownloader
{
	readonly List<SFReusableChunk> m_ChunkDatas = new();

	readonly string m_Qrmk;

	int m_NextChunkToDownloadIndex;

	int m_NextChunkToConsumeIndex;

	// External cancellation token, used to stop donwload
	readonly CancellationToken m_ExternalCancellationToken;

	readonly int m_PrefetchSlot;

	readonly IRestRequester m_RestRequester;

	readonly Dictionary<string, string> m_ChunkHeaders;

	readonly List<ExecResponseChunk> m_ChunkInfos;

	readonly List<Task<IResultChunk>> m_TaskQueues;

	public SFBlockingChunkDownloaderV3(int colCount, List<ExecResponseChunk> chunkInfos, string qrmk, Dictionary<string, string> chunkHeaders, CancellationToken cancellationToken, SFBaseResultSet resultSet)
	{
		if (resultSet.SFStatement == null)
			throw new ArgumentException($"resultSet.SFStatement is null", nameof(resultSet));

		m_Qrmk = qrmk;
		m_ChunkHeaders = chunkHeaders;
		m_NextChunkToDownloadIndex = 0;
		m_RestRequester = resultSet.SFStatement.SFSession.RestRequester;
		m_PrefetchSlot = Math.Min(chunkInfos.Count, GetPrefetchThreads(resultSet));
		m_ChunkInfos = chunkInfos;
		m_NextChunkToConsumeIndex = 0;
		m_TaskQueues = new List<Task<IResultChunk>>();
		m_ExternalCancellationToken = cancellationToken;

		for (var i = 0; i < m_PrefetchSlot; i++)
		{
			var reusableChunk = new SFReusableChunk(colCount);
			reusableChunk.Reset(chunkInfos[m_NextChunkToDownloadIndex], m_NextChunkToDownloadIndex);
			m_ChunkDatas.Add(reusableChunk);

			m_TaskQueues.Add(DownloadChunkAsync(new DownloadContextV3()
			{
				chunk = reusableChunk,
				qrmk = m_Qrmk,
				chunkHeaders = m_ChunkHeaders,
				cancellationToken = m_ExternalCancellationToken
			}));

			m_NextChunkToDownloadIndex++;
		}
	}

	static int GetPrefetchThreads(SFBaseResultSet resultSet)
	{
		if (resultSet.SFStatement == null)
			throw new ArgumentException($"resultSet.SFStatement is null", nameof(resultSet));

		var sessionParameters = resultSet.SFStatement.SFSession.ParameterMap;
		var val = (string)sessionParameters[SFSessionParameter.CLIENT_PREFETCH_THREADS]!;
		return int.Parse(val, CultureInfo.InvariantCulture);
	}

	/*public Task<IResultChunk> GetNextChunkAsync()
	{
		return _downloadTasks.IsCompleted ? Task.FromResult<SFResultChunk>(null) : _downloadTasks.Take();
	}*/

	public async Task<IResultChunk?> GetNextChunkAsync()
	{
		if (m_NextChunkToConsumeIndex < m_ChunkInfos.Count)
		{
			var chunk = m_TaskQueues[m_NextChunkToConsumeIndex % m_PrefetchSlot];

			if (m_NextChunkToDownloadIndex < m_ChunkInfos.Count && m_NextChunkToConsumeIndex > 0)
			{
				var reusableChunk = m_ChunkDatas[m_NextChunkToDownloadIndex % m_PrefetchSlot];
				reusableChunk.Reset(m_ChunkInfos[m_NextChunkToDownloadIndex], m_NextChunkToDownloadIndex);

				m_TaskQueues[m_NextChunkToDownloadIndex % m_PrefetchSlot] = DownloadChunkAsync(new DownloadContextV3()
				{
					chunk = reusableChunk,
					qrmk = this.m_Qrmk,
					chunkHeaders = this.m_ChunkHeaders,
					cancellationToken = m_ExternalCancellationToken
				});
				m_NextChunkToDownloadIndex++;
			}

			m_NextChunkToConsumeIndex++;
			return await chunk.ConfigureAwait(false); //await is needed to cast from Task<IResultChunk> to Task<IResultChunk?>
		}
		else
		{
			return await Task.FromResult<IResultChunk?>(null).ConfigureAwait(false);
		}
	}

	async Task<IResultChunk> DownloadChunkAsync(DownloadContextV3 downloadContext)
	{
		if (downloadContext.chunk == null)
			throw new ArgumentException("downloadContext.chunk is null", nameof(downloadContext));

		var chunk = downloadContext.chunk;

		var downloadRequest = new S3DownloadRequest()
		{
			Url = new UriBuilder(chunk.Url!).Uri,
			Qrmk = downloadContext.qrmk,
			// s3 download request timeout to one hour
			RestTimeout = TimeSpan.FromHours(1),
			HttpTimeout = Timeout.InfiniteTimeSpan, // Disable timeout for each request
			ChunkHeaders = downloadContext.chunkHeaders
		};

		using (var httpResponse = await m_RestRequester.GetAsync(downloadRequest, downloadContext.cancellationToken).ConfigureAwait(false))
		using (var stream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false))
		{
			await ParseStreamIntoChunk(stream, chunk).ConfigureAwait(false);
		}
		return chunk;
	}

	/// <summary>
	///     Content from s3 in format of
	///     ["val1", "val2", null, ...],
	///     ["val3", "val4", null, ...],
	///     ...
	///     To parse it as a json, we need to preappend '[' and append ']' to the stream
	/// </summary>
	/// <param name="content"></param>
	/// <param name="resultChunk"></param>
	static async Task ParseStreamIntoChunk(Stream content, IResultChunk resultChunk)
	{
		await new ReusableChunkParser(content).ParseChunkAsync(resultChunk).ConfigureAwait(false);
	}
}
