﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using Newtonsoft.Json;

namespace Tortuga.Data.Snowflake.Core.Messages;

class LoginResponse : BaseRestResponse
{
	[JsonProperty(PropertyName = "data")]
	internal LoginResponseData? Data { get; set; }
}
