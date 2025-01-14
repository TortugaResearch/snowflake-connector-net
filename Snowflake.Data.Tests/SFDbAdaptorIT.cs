﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using NUnit.Framework;
using System.Data;
using System.Runtime.InteropServices;

namespace Tortuga.Data.Snowflake.Tests;

[TestFixture]
class SFDbAdaptorIT : SFBaseTest
{
	[Test]
	public void TestSelectStatement()
	{
		DataSet ds = new DataSet("ds");
		using (SnowflakeDbConnection conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;
			conn.Open();

			IDbDataAdapter adaptor = new SnowlfakeDbDataAdapter("select 1 as col1, 2 AS col2", conn);
			adaptor.Fill(ds);
			conn.Close();
		}
		Assert.AreEqual(ds.Tables[0].TableName, "Table");
		Assert.AreEqual(ds.Tables[0].Rows[0].ItemArray[0], 1);
		Assert.AreEqual(ds.Tables[0].Rows[0].ItemArray[1], 2);

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Assert.AreEqual(ds.Tables[0].Rows[0]["col1"].ToString(), "1");
			Assert.AreEqual(ds.Tables[0].Rows[0]["col2"].ToString(), "2");
		}
	}
}
