﻿/*
 * Copyright (c) 2012-2021 Snowflake Computing Inc. All rights reserved.
 */

using NUnit.Framework;
using System.Data;

namespace Tortuga.Data.Snowflake.Tests;

[TestFixture]
class SFDbCommandIT : SFBaseTest
{
	[Test]
	public void TestSimpleCommand()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;

			conn.Open();
			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "select 1";

			// command type can only be text, stored procedure are not supported.
			Assert.AreEqual(CommandType.Text, cmd.CommandType);
			try
			{
				cmd.CommandType = CommandType.StoredProcedure;
				Assert.Fail();
			}
			catch (NotSupportedException)
			{
			}

			Assert.AreEqual(UpdateRowSource.None, cmd.UpdatedRowSource);
			try
			{
				cmd.UpdatedRowSource = UpdateRowSource.FirstReturnedRecord;
				Assert.Fail();
			}
			catch (NotSupportedException)
			{
			}

			Assert.AreSame(conn, cmd.Connection);
			try
			{
				cmd.Connection = null;
				Assert.Fail();
			}
			catch (InvalidOperationException)
			{
			}

			Assert.IsFalse(((SnowflakeDbCommand)cmd).DesignTimeVisible);
			try
			{
				((SnowflakeDbCommand)cmd).DesignTimeVisible = true;
				Assert.Fail();
			}
			catch (NotSupportedException)
			{
			}

			object val = cmd.ExecuteScalar();
			Assert.AreEqual(1L, (long)val);

			conn.Close();
		}
	}

	[Test]
	// Skip SimpleLargeResultSet test on GCP as it will fail
	// on row 8192 consistently on Appveyor.
	[IgnoreOnEnvIs("snowflake_cloud_env", new string[] { "GCP" })]
	public void TestSimpleLargeResultSet()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;

			conn.Open();

			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "select seq4(), uniform(1, 10, 42) from table(generator(rowcount => 1000000)) v order by 1";
			IDataReader reader = cmd.ExecuteReader();
			int counter = 0;
			while (reader.Read())
			{
				Assert.AreEqual(counter.ToString(), reader.GetString(0));
				counter++;
			}
			conn.Close();
		}
	}

	/*
 * Disabled to make sure that configuration changes does not cause problems with appveyor
 *
[Test]
public void TestUseV1ResultParser()
{
	SFConfiguration.Instance().UseV2JsonParser = false;

	using (var conn = new SnowflakeDbConnection())
	{
		conn.ConnectionString = ConnectionString;

		conn.Open();

		IDbCommand cmd = conn.CreateCommand();
		cmd.CommandText = "select seq4(), uniform(1, 10, 42) from table(generator(rowcount => 200000)) v order by 1";
		IDataReader reader = cmd.ExecuteReader();
		int counter = 0;
		while (reader.Read())
		{
			Assert.AreEqual(counter.ToString(), reader.GetString(0));
			counter++;
		}
		conn.Close();
	}
	SFConfiguration.Instance().UseV2JsonParser = true;
}

[Test]
public void TestUseV2ChunkDownloader()
{
	SFConfiguration.Instance().UseV2ChunkDownloader = true;

	using (var conn = new SnowflakeDbConnection())
	{
		conn.ConnectionString = ConnectionString;

		conn.Open();

		IDbCommand cmd = conn.CreateCommand();
		cmd.CommandText = "select seq4(), uniform(1, 10, 42) from table(generator(rowcount => 200000)) v order by 1";
		IDataReader reader = cmd.ExecuteReader();
		int counter = 0;
		while (reader.Read())
		{
			Assert.AreEqual(counter.ToString(), reader.GetString(0));
			counter++;
		}
		conn.Close();
	}
	SFConfiguration.Instance().UseV2ChunkDownloader = false;
}
*/

	[Test]
	public void TestDataSourceError()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;

			conn.Open();

			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "select * from table_not_exists";
			try
			{
				IDataReader reader = cmd.ExecuteReader();
				Assert.Fail();
			}
			catch (SnowflakeDbException e)
			{
				Assert.AreEqual(2003, e.ErrorCode);
				Assert.AreNotEqual("", e.QueryId);
			}

			conn.Close();
		}
	}

	[Test]
	public void TestCancelQuery()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;

			conn.Open();

			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "select count(seq4()) from table(generator(timelimit => 20)) v";
			Task executionThread = Task.Run(() =>
			{
				try
				{
					cmd.ExecuteScalar();
					Assert.Fail();
				}
				catch (SnowflakeDbException e)
				{
					// 604 is error code from server meaning query has been canceled
					if (604 != e.ErrorCode)
					{
						Assert.Fail($"Unexpected error code {e.ErrorCode} for {e.Message}");
					}
				}
			});

			Thread.Sleep(8000);
			cmd.Cancel();

			try
			{
				executionThread.Wait();
			}
			catch (AggregateException e)
			{
				if (e.InnerException.GetType() != typeof(NUnit.Framework.AssertionException))
				{
					Assert.AreEqual(
					"System.Threading.Tasks.TaskCanceledException",
					e.InnerException.GetType().ToString());
				}
				else
				{
					// Unexpected exception
					throw;
				}
			}

			conn.Close();
		}
	}

	[Test]
	public void TestTransaction()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;

			conn.Open();

			try
			{
				conn.BeginTransaction(IsolationLevel.ReadUncommitted);
				Assert.Fail();
			}
			catch (ArgumentOutOfRangeException)
			{
			}

			IDbTransaction tran = conn.BeginTransaction(IsolationLevel.ReadCommitted);

			IDbCommand command = conn.CreateCommand();
			command.Transaction = tran;
			command.CommandText = "create or replace table testtransaction(cola string)";
			command.ExecuteNonQuery();
			command.Transaction.Commit();

			command.CommandText = "show tables like 'testtransaction'";
			IDataReader reader = command.ExecuteReader();
			Assert.IsTrue(reader.Read());
			Assert.IsFalse(reader.Read());

			// start another transaction to test rollback
			tran = conn.BeginTransaction(IsolationLevel.ReadCommitted);
			command.Transaction = tran;
			command.CommandText = "insert into testtransaction values('test')";

			command.ExecuteNonQuery();
			command.CommandText = "select * from testtransaction";
			reader = command.ExecuteReader();
			Assert.IsTrue(reader.Read());
			Assert.AreEqual("test", reader.GetString(0));
			command.Transaction.Rollback();

			// no value will be in table since it has been rollbacked
			command.CommandText = "select * from testtransaction";
			reader = command.ExecuteReader();
			Assert.IsFalse(reader.Read());

			conn.Close();
		}
	}

	[Test]
	public void TestRowsAffected()
	{
		String[] testCommands =
		{
				"create or replace table test_rows_affected(cola int, colb string)",
				"insert into test_rows_affected values(1, 'a'),(2, 'b')",
				"merge into test_rows_affected using (select 1 as cola, 'c' as colb) m on " +
				"test_rows_affected.cola = m.cola when matched then update set test_rows_affected.colb='update' " +
				"when not matched then insert (cola, colb) values (3, 'd')",
				"drop table if exists test_rows_affected"
			};

		int[] expectedResult =
		{
				0, 2, 1, 0
			};

		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;

			conn.Open();

			using (IDbCommand command = conn.CreateCommand())
			{
				int rowsAffected = -1;
				for (int i = 0; i < testCommands.Length; i++)
				{
					command.CommandText = testCommands[i];
					rowsAffected = command.ExecuteNonQuery();

					Assert.AreEqual(expectedResult[i], rowsAffected);
				}
			}
			conn.Close();
		}
	}

	[Test]
	public void TestExecuteScalarNull()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;
			conn.Open();

			using (IDbCommand command = conn.CreateCommand())
			{
				command.CommandText = "select 1 where 2 > 3";
				object val = command.ExecuteScalar();

				Assert.AreEqual(DBNull.Value, val);
			}
			conn.Close();
		}
	}

	[Test]
	public void TestCreateCommandBeforeOpeningConnection()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;

			using (var command = conn.CreateCommand())
			{
				conn.Open();
				command.CommandText = "select 1";
				Assert.DoesNotThrow(() => command.ExecuteNonQuery());
			}
		}
	}

	[Test]
	public void TestRowsAffectedUnload()
	{
		using (var conn = new SnowflakeDbConnection())
		{
			conn.ConnectionString = ConnectionString;
			conn.Open();

			using (IDbCommand command = conn.CreateCommand())
			{
				command.CommandText = "create or replace table test_rows_affected_unload(c1 number)";
				command.ExecuteNonQuery();

				command.CommandText = "insert into test_rows_affected_unload values(1), (2), (3), (4), (5), (6)";
				command.ExecuteNonQuery();

				command.CommandText = "drop stage if exists my_unload_stage";
				command.ExecuteNonQuery();

				command.CommandText = "create stage if not exists my_unload_stage";
				command.ExecuteNonQuery();

				command.CommandText = "copy into @my_unload_stage/unload/ from test_rows_affected_unload;";
				int affected = command.ExecuteNonQuery();

				Assert.AreEqual(6, affected);

				command.CommandText = "drop stage if exists my_unload_stage";
				command.ExecuteNonQuery();

				command.CommandText = "drop table if exists test_rows_affected_unload";
				command.ExecuteNonQuery();
			}
			conn.Close();
		}
	}
}
