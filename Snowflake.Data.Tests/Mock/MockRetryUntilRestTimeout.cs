﻿/*
 * Copyright (c) 2021 Snowflake Computing Inc. All rights reserved.
 */

using Tortuga.Data.Snowflake.Core;
using Tortuga.Data.Snowflake.Core.RequestProcessing;

namespace Tortuga.Data.Snowflake.Tests.Mock;

class MockRetryUntilRestTimeoutRestRequester : RestRequester, IMockRestRequester
{
	public MockRetryUntilRestTimeoutRestRequester() : base(null)
	{
		// Does nothing
	}

	public void setHttpClient(HttpClient httpClient)
	{
		base._HttpClient = httpClient;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,
														  TimeSpan restTimeout,
														  CancellationToken externalCancellationToken)
	{
		// Override the http timeout and set to 1ms to force all http request to timeout and retry
		message.SetOption(RestRequest.HTTP_REQUEST_TIMEOUT_KEY, TimeSpan.FromMilliseconds(1));
		return await (base.SendAsync(message, restTimeout, externalCancellationToken).ConfigureAwait(false));
	}

	protected override HttpResponseMessage Send(HttpRequestMessage message,
													  TimeSpan restTimeout,
													  CancellationToken externalCancellationToken)
	{
		// Override the http timeout and set to 1ms to force all http request to timeout and retry
		message.SetOption(RestRequest.HTTP_REQUEST_TIMEOUT_KEY, TimeSpan.FromMilliseconds(1));
		return base.Send(message, restTimeout, externalCancellationToken);
	}
}
