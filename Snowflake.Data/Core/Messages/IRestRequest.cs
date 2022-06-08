﻿/*
 * Copyright (c) 2012-2021 Snowflake Computing Inc. All rights reserved.
 */

#nullable enable

namespace Tortuga.Data.Snowflake.Core.Messages;

internal interface IRestRequest
{
	HttpRequestMessage ToRequestMessage(HttpMethod method);

	TimeSpan GetRestTimeout();
}
