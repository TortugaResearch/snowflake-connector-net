﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using NUnit.Framework;
using System.Text;
using Tortuga.Data.Snowflake.Core.ResponseProcessing;
using Tortuga.Data.Snowflake.Core.ResponseProcessing.Chunks;

namespace Tortuga.Data.Snowflake.Tests;

[TestFixture]
class SFReusableChunkTest
{
	[Test]
	public async Task TestSimpleChunk()
	{
		var data = "[ [\"1\", \"1.234\", \"abcde\"],  [\"2\", \"5.678\", \"fghi\"] ]";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = 100,
			rowCount = 2
		};

		var chunk = new SFReusableChunk(3);
		chunk.Reset(chunkInfo, 0);

		await parser.ParseChunkAsync(chunk);

		Assert.AreEqual("1", chunk.ExtractCell(0, 0).SafeToString());
		Assert.AreEqual("1.234", chunk.ExtractCell(0, 1).SafeToString());
		Assert.AreEqual("abcde", chunk.ExtractCell(0, 2).SafeToString());
		Assert.AreEqual("2", chunk.ExtractCell(1, 0).SafeToString());
		Assert.AreEqual("5.678", chunk.ExtractCell(1, 1).SafeToString());
		Assert.AreEqual("fghi", chunk.ExtractCell(1, 2).SafeToString());
	}

	[Test]
	public async Task TestChunkWithNull()
	{
		var data = "[ [null, \"1.234\", null],  [\"2\", null, \"fghi\"] ]";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = 100,
			rowCount = 2
		};

		var chunk = new SFReusableChunk(3);
		chunk.Reset(chunkInfo, 0);

		await parser.ParseChunkAsync(chunk);

		Assert.AreEqual(null, chunk.ExtractCell(0, 0).SafeToString());
		Assert.AreEqual("1.234", chunk.ExtractCell(0, 1).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(0, 2).SafeToString());
		Assert.AreEqual("2", chunk.ExtractCell(1, 0).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(1, 1).SafeToString());
		Assert.AreEqual("fghi", chunk.ExtractCell(1, 2).SafeToString());
	}

	[Test]
	public async Task TestChunkWithDate()
	{
		var data = "[ [null, \"2019-08-21T11:58:00\", null],  [\"2\", null, \"fghi\"] ]";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = 100,
			rowCount = 2
		};

		var chunk = new SFReusableChunk(3);
		chunk.Reset(chunkInfo, 0);

		await parser.ParseChunkAsync(chunk);

		Assert.AreEqual(null, chunk.ExtractCell(0, 0).SafeToString());
		Assert.AreEqual("2019-08-21T11:58:00", chunk.ExtractCell(0, 1).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(0, 2).SafeToString());
		Assert.AreEqual("2", chunk.ExtractCell(1, 0).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(1, 1).SafeToString());
		Assert.AreEqual("fghi", chunk.ExtractCell(1, 2).SafeToString());
	}

	[Test]
	public async Task TestChunkWithEscape()
	{
		var data = "[ [\"\\\\åäö\\nÅÄÖ\\r\", \"1.234\", null],  [\"2\", null, \"fghi\"] ]";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = bytes.Length,
			rowCount = 2
		};

		var chunk = new SFReusableChunk(3);
		chunk.Reset(chunkInfo, 0);

		await parser.ParseChunkAsync(chunk);

		Assert.AreEqual("\\åäö\nÅÄÖ\r", chunk.ExtractCell(0, 0).SafeToString());
		Assert.AreEqual("1.234", chunk.ExtractCell(0, 1).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(0, 2).SafeToString());
		Assert.AreEqual("2", chunk.ExtractCell(1, 0).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(1, 1).SafeToString());
		Assert.AreEqual("fghi", chunk.ExtractCell(1, 2).SafeToString());
	}

	[Test]
	public async Task TestChunkWithLongString()
	{
		var longstring = new string('å', 10 * 1000 * 1000);
		var data = "[ [\"åäö\\nÅÄÖ\\r\", \"1.234\", null],  [\"2\", null, \"" + longstring + "\"] ]";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = bytes.Length,
			rowCount = 2
		};

		var chunk = new SFReusableChunk(3);
		chunk.Reset(chunkInfo, 0);

		await parser.ParseChunkAsync(chunk);

		Assert.AreEqual("åäö\nÅÄÖ\r", chunk.ExtractCell(0, 0).SafeToString());
		Assert.AreEqual("1.234", chunk.ExtractCell(0, 1).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(0, 2).SafeToString());
		Assert.AreEqual("2", chunk.ExtractCell(1, 0).SafeToString());
		Assert.AreEqual(null, chunk.ExtractCell(1, 1).SafeToString());
		Assert.AreEqual(longstring, chunk.ExtractCell(1, 2).SafeToString());
	}

	[Test]
	public async Task TestParserError1()
	{
		// Unterminated escape sequence
		var data = "[ [\"åäö\\";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = bytes.Length,
			rowCount = 1
		};

		var chunk = new SFReusableChunk(1);
		chunk.Reset(chunkInfo, 0);

		try
		{
			await parser.ParseChunkAsync(chunk);
			Assert.Fail();
		}
		catch (SnowflakeDbException e)
		{
			Assert.AreEqual(SnowflakeDbError.InternalError, e.SnowflakeError);
		}
	}

	[Test]
	public async Task TestParserError2()
	{
		// Unterminated string
		var data = "[ [\"åäö";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = bytes.Length,
			rowCount = 1
		};

		var chunk = new SFReusableChunk(1);
		chunk.Reset(chunkInfo, 0);

		try
		{
			await parser.ParseChunkAsync(chunk);
			Assert.Fail();
		}
		catch (SnowflakeDbException e)
		{
			Assert.AreEqual(SnowflakeDbError.InternalError, e.SnowflakeError);
		}
	}

	[Test]
	public async Task TestParserWithTab()
	{
		// Unterminated string
		var data = "[[\"abc\t\"]]";
		var bytes = Encoding.UTF8.GetBytes(data);
		var stream = new MemoryStream(bytes);
		var parser = new ReusableChunkParser(stream);

		var chunkInfo = new ExecResponseChunk()
		{
			url = "fake",
			uncompressedSize = bytes.Length,
			rowCount = 1
		};

		var chunk = new SFReusableChunk(1);
		chunk.Reset(chunkInfo, 0);

		await parser.ParseChunkAsync(chunk);
		Assert.AreEqual("abc\t", chunk.ExtractCell(0, 0).SafeToString());
	}
}
