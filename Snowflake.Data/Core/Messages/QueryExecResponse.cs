﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using Newtonsoft.Json;

namespace Tortuga.Data.Snowflake.Core.Messages;

class QueryExecResponse : BaseRestResponse
{
	[JsonProperty(PropertyName = "data")]
	internal QueryExecResponseData? Data { get; set; }
}
