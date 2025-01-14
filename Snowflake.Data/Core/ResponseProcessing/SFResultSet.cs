﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using Tortuga.Data.Snowflake.Core.Messages;
using Tortuga.Data.Snowflake.Core.RequestProcessing;
using Tortuga.Data.Snowflake.Core.ResponseProcessing.Chunks;

namespace Tortuga.Data.Snowflake.Core.ResponseProcessing;

class SFResultSet : SFBaseResultSet
{
	int m_CurrentChunkRowIdx;

	int m_CurrentChunkRowCount;

	readonly IChunkDownloader? m_ChunkDownloader;

	IResultChunk m_CurrentChunk;

	public SFResultSet(QueryExecResponse response, SFStatement sfStatement, CancellationToken cancellationToken) : base(sfStatement.SFSession.Configuration, SFStatement.BuildQueryStatusFromQueryResponse(response))
	{
		if (response.Data == null)
			throw new ArgumentException($"response.Data is null", nameof(response));

		//if (response.responseData.RowType == null)
		//	throw new ArgumentException($"responseData.rowType is null", nameof(responseData));
		//if (response.responseData.RowSet == null)
		//	throw new ArgumentException($"responseData.rowSet is null", nameof(responseData));

		var responseData = response.Data;
		// async result will not provide parameters, so need to set
		responseData.Parameters = responseData.Parameters ?? new List<NameValueParameter>();

		m_ColumnCount = responseData.RowType?.Count ?? 0;
		m_CurrentChunkRowIdx = -1;
		m_CurrentChunkRowCount = responseData.RowSet?.GetLength(0) ?? 0;

		SFStatement = sfStatement;
		updateSessionStatus(responseData);

		if (responseData.Chunks != null)
		{
			// counting the first chunk
			//_totalChunkCount = responseData.chunks.Count;
			m_ChunkDownloader = GetDownloader(responseData, cancellationToken);
		}

		m_CurrentChunk = new SFResultChunk(responseData.RowSet);
		responseData.RowSet = null;

		SFResultSetMetaData = new SFResultSetMetaData(responseData);

		m_IsClosed = false;

		m_QueryId = responseData.QueryId;
	}

	readonly string[] PutGetResponseRowTypeInfo = {
			"SourceFileName",
			"DestinationFileName",
			"SourceFileSize",
			"DestinationFileSize",
			"SourceCompressionType",
			"DestinationCompressionType",
			"ResultStatus",
			"ErrorDetails"
		};

	public void initializePutGetRowType(List<ExecResponseRowType> rowType)
	{
		foreach (var name in PutGetResponseRowTypeInfo)
		{
			rowType.Add(new ExecResponseRowType()
			{
				Name = name,
				Type = "text"
			});
		}
	}

	public SFResultSet(PutGetResponseData responseData, SFStatement sfStatement, CancellationToken cancellationToken) : base(sfStatement.SFSession.Configuration, null)
	{
		if (responseData.RowSet == null)
			throw new ArgumentException($"responseData.rowSet is null", nameof(responseData));

		responseData.RowType = new List<ExecResponseRowType>();
		initializePutGetRowType(responseData.RowType);

		m_ColumnCount = responseData.RowType.Count;
		m_CurrentChunkRowIdx = -1;
		m_CurrentChunkRowCount = responseData.RowSet.GetLength(0);

		this.SFStatement = sfStatement;

		m_CurrentChunk = new SFResultChunk(responseData.RowSet);
		responseData.RowSet = null;

		SFResultSetMetaData = new SFResultSetMetaData(responseData);

		m_IsClosed = false;

		m_QueryId = responseData.QueryId;
	}

	internal void resetChunkInfo(IResultChunk nextChunk)
	{
		if (m_CurrentChunk is SFResultChunk chunk)
			chunk.RowSet = null;

		m_CurrentChunk = nextChunk;
		m_CurrentChunkRowIdx = 0;
		m_CurrentChunkRowCount = m_CurrentChunk.GetRowCount();
	}

	internal override async Task<bool> NextAsync()
	{
		if (m_IsClosed)
		{
			throw new SnowflakeDbException(SnowflakeDbError.DataReaderAlreadyClosed);
		}

		m_CurrentChunkRowIdx++;
		if (m_CurrentChunkRowIdx < m_CurrentChunkRowCount)
		{
			return true;
		}

		if (m_ChunkDownloader != null)
		{
			// GetNextChunk could be blocked if download result is not done yet.
			// So put this piece of code in a seperate task
			var nextChunk = await m_ChunkDownloader.GetNextChunkAsync().ConfigureAwait(false);
			if (nextChunk != null)
			{
				resetChunkInfo(nextChunk);
				return true;
			}
			else
			{
				return false;
			}
		}

		return false;
	}

	internal override bool Next()
	{
		if (m_IsClosed)
		{
			throw new SnowflakeDbException(SnowflakeDbError.DataReaderAlreadyClosed);
		}

		m_CurrentChunkRowIdx++;
		if (m_CurrentChunkRowIdx < m_CurrentChunkRowCount)
		{
			return true;
		}

		if (m_ChunkDownloader != null)
		{
			var nextChunk = Task.Run(async () => await (m_ChunkDownloader.GetNextChunkAsync()).ConfigureAwait(false)).Result;
			if (nextChunk != null)
			{
				resetChunkInfo(nextChunk);
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Move cursor back one row.
	/// </summary>
	/// <returns>True if it works, false otherwise.</returns>
	internal override bool Rewind()
	{
		if (m_IsClosed)
		{
			throw new SnowflakeDbException(SnowflakeDbError.DataReaderAlreadyClosed);
		}

		if (m_CurrentChunkRowIdx >= 0)
		{
			m_CurrentChunkRowIdx--;
			if (m_CurrentChunkRowIdx >= m_CurrentChunkRowCount)
			{
				return true;
			}
		}

		return false;
	}

	protected override UTF8Buffer? getObjectInternal(int columnIndex)
	{
		if (m_IsClosed)
		{
			throw new SnowflakeDbException(SnowflakeDbError.DataReaderAlreadyClosed);
		}

		if (columnIndex < 0 || columnIndex >= m_ColumnCount)
		{
			throw new SnowflakeDbException(SnowflakeDbError.ColumnIndexOutOfBound, columnIndex);
		}

		return m_CurrentChunk.ExtractCell(m_CurrentChunkRowIdx, columnIndex);
	}

	void updateSessionStatus(QueryExecResponseData responseData)
	{
		if (SFStatement == null)
			throw new InvalidOperationException($"{nameof(SFStatement)} is null");

		var session = SFStatement.SFSession;
		session.m_Database = responseData.FinalDatabaseName;
		session.m_Schema = responseData.FinalSchemaName;

		if (responseData.Parameters != null)
			session.UpdateSessionParameterMap(responseData.Parameters);
	}

	IChunkDownloader GetDownloader(QueryExecResponseData responseData, CancellationToken cancellationToken)
	{
		var ChunkDownloaderVersion = Configuration.ChunkDownloaderVersion;
		if (Configuration.UseV2ChunkDownloader)
			ChunkDownloaderVersion = 2;

		switch (ChunkDownloaderVersion)
		{
			case 1:
				return new SFBlockingChunkDownloader(responseData.RowType!.Count,
					responseData.Chunks!,
					responseData.Qrmk!,
					responseData.ChunkHeaders!,
					cancellationToken,
					this);

			case 2:

				if (SFStatement == null)
					throw new InvalidOperationException($"{nameof(SFStatement)} is null");

				return new SFChunkDownloaderV2(responseData.RowType!.Count,
					responseData.Chunks!,
					responseData.Qrmk!,
					responseData.ChunkHeaders!,
					cancellationToken,
					SFStatement.SFSession.RestRequester, Configuration);

			default:
				return new SFBlockingChunkDownloaderV3(responseData.RowType!.Count,
					responseData.Chunks!,
					responseData.Qrmk!,
					responseData.ChunkHeaders!,
					cancellationToken,
					this);
		}
	}
}
