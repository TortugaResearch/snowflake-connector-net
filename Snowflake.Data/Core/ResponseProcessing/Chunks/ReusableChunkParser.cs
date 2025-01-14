﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

namespace Tortuga.Data.Snowflake.Core.ResponseProcessing.Chunks;

class ReusableChunkParser : ChunkParser
{
	// Very fast parser, only supports strings and nulls
	// Never generates parsing errors

	readonly Stream stream;

	internal ReusableChunkParser(Stream stream)
	{
		this.stream = stream;
	}

	public override void ParseChunk(IResultChunk chunk)
	{
		SFReusableChunk rc = (SFReusableChunk)chunk;

		var inString = false;
		int c;
		var input = new FastStreamWrapper(stream);
		var ms = new FastMemoryStream();
		while ((c = input.ReadByte()) >= 0)
		{
			if (!inString)
			{
				// n means null
				// " quote means begin string
				// all else are ignored
				if (c == '"')
				{
					inString = true;
				}
				else if (c == 'n')
				{
					rc.AddCell(null, 0);
				}
				// ignore anything else
			}
			else
			{
				// Inside a string, look for end string
				// Anything else is saved in the buffer
				if (c == '"')
				{
					rc.AddCell(ms.GetBuffer(), ms.Length);
					ms.Clear();
					inString = false;
				}
				else if (c == '\\')
				{
					// Process next character
					c = input.ReadByte();
					switch (c)
					{
						case 'n':
							c = '\n';
							break;

						case 'r':
							c = '\r';
							break;

						case 'b':
							c = '\b';
							break;

						case 't':
							c = '\t';
							break;

						case -1:
							throw new SnowflakeDbException(SnowflakeDbError.InternalError, $"Unexpected end of stream in escape sequence");
					}
					ms.WriteByte((byte)c);
				}
				else
				{
					ms.WriteByte((byte)c);
				}
			}
		}
		if (inString)
			throw new SnowflakeDbException(SnowflakeDbError.InternalError, $"Unexpected end of stream in string");
	}

	public override async Task ParseChunkAsync(IResultChunk chunk)
	{
		await Task.Run(() => ParseChunk(chunk)).ConfigureAwait(false);
	}
}
