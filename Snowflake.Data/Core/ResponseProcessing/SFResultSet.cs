﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using Tortuga.Data.Snowflake.Core.Messages;
using Tortuga.Data.Snowflake.Core.RequestProcessing;
using Tortuga.Data.Snowflake.Core.ResponseProcessing.Chunks;
using Tortuga.Data.Snowflake.Core.Sessions;

namespace Tortuga.Data.Snowflake.Core.ResponseProcessing;

class SFResultSet : SFBaseResultSet
{
	private int _currentChunkRowIdx;

	private int _currentChunkRowCount;

	private readonly int _totalChunkCount;

	private readonly IChunkDownloader _chunkDownloader;

	private IResultChunk _currentChunk;

	public SFResultSet(QueryExecResponseData responseData, SFStatement sfStatement, CancellationToken cancellationToken) : base(sfStatement.SfSession.Configuration)
	{
		columnCount = responseData.rowType.Count;
		_currentChunkRowIdx = -1;
		_currentChunkRowCount = responseData.rowSet.GetLength(0);

		this.sfStatement = sfStatement;
		updateSessionStatus(responseData);

		if (responseData.chunks != null)
		{
			// counting the first chunk
			_totalChunkCount = responseData.chunks.Count;
			_chunkDownloader = ChunkDownloaderFactory.GetDownloader(responseData, this, cancellationToken);
		}

		_currentChunk = new SFResultChunk(responseData.rowSet);
		responseData.rowSet = null;

		sfResultSetMetaData = new SFResultSetMetaData(responseData);

		isClosed = false;

		queryId = responseData.queryId;
	}

	string[] PutGetResponseRowTypeInfo = {
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
		foreach (string name in PutGetResponseRowTypeInfo)
		{
			rowType.Add(new ExecResponseRowType()
			{
				name = name,
				type = "text"
			});
		}
	}

	public SFResultSet(PutGetResponseData responseData, SFStatement sfStatement, CancellationToken cancellationToken) : base(sfStatement.SfSession.Configuration)
	{
		responseData.rowType = new List<ExecResponseRowType>();
		initializePutGetRowType(responseData.rowType);

		columnCount = responseData.rowType.Count;
		_currentChunkRowIdx = -1;
		_currentChunkRowCount = responseData.rowSet.GetLength(0);

		this.sfStatement = sfStatement;

		_currentChunk = new SFResultChunk(responseData.rowSet);
		responseData.rowSet = null;

		sfResultSetMetaData = new SFResultSetMetaData(responseData);

		isClosed = false;

		queryId = responseData.queryId;
	}

	internal void resetChunkInfo(IResultChunk nextChunk)
	{
		if (_currentChunk is SFResultChunk)
		{
			((SFResultChunk)_currentChunk).RowSet = null;
		}
		_currentChunk = nextChunk;
		_currentChunkRowIdx = 0;
		_currentChunkRowCount = _currentChunk.GetRowCount();
	}

	internal override async Task<bool> NextAsync()
	{
		if (isClosed)
		{
			throw new SnowflakeDbException(SFError.DATA_READER_ALREADY_CLOSED);
		}

		_currentChunkRowIdx++;
		if (_currentChunkRowIdx < _currentChunkRowCount)
		{
			return true;
		}

		if (_chunkDownloader != null)
		{
			// GetNextChunk could be blocked if download result is not done yet.
			// So put this piece of code in a seperate task
			var nextChunk = await _chunkDownloader.GetNextChunkAsync().ConfigureAwait(false);
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
		if (isClosed)
		{
			throw new SnowflakeDbException(SFError.DATA_READER_ALREADY_CLOSED);
		}

		_currentChunkRowIdx++;
		if (_currentChunkRowIdx < _currentChunkRowCount)
		{
			return true;
		}

		if (_chunkDownloader != null)
		{
			var nextChunk = Task.Run(async () => await (_chunkDownloader.GetNextChunkAsync()).ConfigureAwait(false)).Result;
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
		if (isClosed)
		{
			throw new SnowflakeDbException(SFError.DATA_READER_ALREADY_CLOSED);
		}

		if (_currentChunkRowIdx >= 0)
		{
			_currentChunkRowIdx--;
			if (_currentChunkRowIdx >= _currentChunkRowCount)
			{
				return true;
			}
		}

		return false;
	}

	protected override UTF8Buffer getObjectInternal(int columnIndex)
	{
		if (isClosed)
		{
			throw new SnowflakeDbException(SFError.DATA_READER_ALREADY_CLOSED);
		}

		if (columnIndex < 0 || columnIndex >= columnCount)
		{
			throw new SnowflakeDbException(SFError.COLUMN_INDEX_OUT_OF_BOUND, columnIndex);
		}

		return _currentChunk.ExtractCell(_currentChunkRowIdx, columnIndex);
	}

	private void updateSessionStatus(QueryExecResponseData responseData)
	{
		SFSession session = this.sfStatement.SfSession;
		session.database = responseData.finalDatabaseName;
		session.schema = responseData.finalSchemaName;

		session.UpdateSessionParameterMap(responseData.parameters);
	}
}
