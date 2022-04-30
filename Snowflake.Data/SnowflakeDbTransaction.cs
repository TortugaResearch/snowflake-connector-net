﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using System.Data;
using System.Data.Common;
using Tortuga.Data.Snowflake.Core;
using Tortuga.Data.Snowflake.Log;

namespace Tortuga.Data.Snowflake;

public class SnowflakeDbTransaction : DbTransaction
{
	private SFLogger logger = SFLoggerFactory.GetLogger<SnowflakeDbTransaction>();

	private IsolationLevel isolationLevel;

	private SnowflakeDbConnection connection;

	private bool disposed = false;

	public SnowflakeDbTransaction(IsolationLevel isolationLevel, SnowflakeDbConnection connection)
	{
		logger.Debug("Begin transaction.");
		if (isolationLevel != IsolationLevel.ReadCommitted)
		{
			throw new SnowflakeDbException(SFError.UNSUPPORTED_FEATURE);
		}

		this.isolationLevel = isolationLevel;
		this.connection = connection;

		using (IDbCommand command = connection.CreateCommand())
		{
			command.CommandText = "BEGIN";
			command.ExecuteNonQuery();
		}
	}

	public override IsolationLevel IsolationLevel
	{
		get
		{
			return isolationLevel;
		}
	}

	protected override DbConnection DbConnection
	{
		get
		{
			return connection;
		}
	}

	public override void Commit()
	{
		logger.Debug("Commit transaction.");
		using (IDbCommand command = connection.CreateCommand())
		{
			command.CommandText = "COMMIT";
			command.ExecuteNonQuery();
		}
	}

	public override void Rollback()
	{
		logger.Debug("Rollback transaction.");
		using (IDbCommand command = connection.CreateCommand())
		{
			command.CommandText = "ROLLBACK";
			command.ExecuteNonQuery();
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposed)
			return;

		// Rollback the uncommitted transaction when the connection is open
		if (connection != null && connection.IsOpen())
		{
			// When there is no uncommitted transaction, Snowflake would just ignore the rollback request;
			this.Rollback();
		}
		disposed = true;

		base.Dispose(disposing);
	}

	~SnowflakeDbTransaction()
	{
		Dispose(false);
	}
}
