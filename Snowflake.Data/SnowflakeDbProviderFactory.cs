﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using System.Data.Common;

namespace Tortuga.Data.Snowflake;

public sealed class SnowflakeDbProviderFactory : DbProviderFactory
{
	public static readonly SnowflakeDbProviderFactory Instance = new();

	/// <summary>
	/// Returns a strongly typed <see cref="DbCommand"/> instance.
	/// </summary>
	public override DbCommand CreateCommand() => new SnowflakeDbCommand();

	/// <summary>
	/// Returns a strongly typed <see cref="DbConnection"/> instance.
	/// </summary>
	public override DbConnection CreateConnection() => new SnowflakeDbConnection();

	/// <summary>
	/// Returns a strongly typed <see cref="DbParameter"/> instance.
	/// </summary>
	public override DbParameter CreateParameter() => new SnowflakeDbParameter();

	/// <summary>
	/// Returns a strongly typed <see cref="DbConnectionStringBuilder"/> instance.
	/// </summary>
	public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new SnowflakeDbConnectionStringBuilder();

	/// <summary>
	/// Returns a strongly typed <see cref="DbCommandBuilder"/> instance.
	/// </summary>
	public override DbCommandBuilder CreateCommandBuilder() => new SnowflakeDbCommandBuilder();

	/// <summary>
	/// Returns a strongly typed <see cref="DbDataAdapter"/> instance.
	/// </summary>
	public override DbDataAdapter CreateDataAdapter() => new SnowlfakeDbDataAdapter();
}
