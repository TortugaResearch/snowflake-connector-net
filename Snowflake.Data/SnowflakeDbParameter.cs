﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Tortuga.Data.Snowflake;

public class SnowflakeDbParameter : DbParameter
{
	string m_ParameterName = "";
	string m_SourceColumn = "";
	readonly SnowflakeDbDataType m_OriginType;

	public SnowflakeDbParameter()
	{
	}

	public SnowflakeDbParameter(string parameterName, SnowflakeDbDataType sfDataType)
	{
		ParameterName = parameterName;
		SFDataType = sfDataType;
		m_OriginType = sfDataType;
	}

	public SnowflakeDbParameter(int parameterIndex, SnowflakeDbDataType sfDataType)
	{
		ParameterName = parameterIndex.ToString(CultureInfo.InvariantCulture);
		SFDataType = sfDataType;
	}

	public override DbType DbType { get; set; }

	public override ParameterDirection Direction
	{
		get => ParameterDirection.Input;

#pragma warning disable CS0809 // Obsolete member overrides non-obsolete member
		[Obsolete("This feature is not supprted.", true)]
		set
#pragma warning restore CS0809 // Obsolete member overrides non-obsolete member
		{
			if (value != ParameterDirection.Input)
				throw new NotSupportedException();
		}
	}

	public override bool IsNullable { get; set; }

	[AllowNull]
	public override string ParameterName { get => m_ParameterName; set => m_ParameterName = value ?? ""; }

	public SnowflakeDbDataType SFDataType { get; set; }
	public override int Size { get; set; }

	[AllowNull]
	public override string SourceColumn { get => m_SourceColumn; set => m_SourceColumn = value ?? ""; }

	public override bool SourceColumnNullMapping { get; set; }

	public override object? Value { get; set; }

	public override void ResetDbType() => SFDataType = m_OriginType;
}
