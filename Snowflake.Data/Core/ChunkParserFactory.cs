﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using Tortuga.Data.Snowflake.Configuration;

namespace Tortuga.Data.Snowflake.Core
{
    class ChunkParserFactory
    {
        public static IChunkParser GetParser(Stream stream)
        {
            if (!SFConfiguration.Instance().UseV2JsonParser)
            {
                return new ChunkDeserializer(stream);
            }
            else
            {
                return new ChunkStreamingParser(stream);
            }
        }
    }
}