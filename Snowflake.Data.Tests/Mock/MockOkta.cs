﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using Tortuga.Data.Snowflake.Core.Authenticators;
using Tortuga.Data.Snowflake.Core.Messages;
using Tortuga.Data.Snowflake.Core.RequestProcessing;

namespace Tortuga.Data.Snowflake.Tests.Mock;

class MockOktaRestRequester : IMockRestRequester
{
	public string TokenUrl { get; set; }
	public string SSOUrl { get; set; }
	public StringContent ResponseContent { get; set; }

	public T Get<T>(RestRequest request)
	{
		throw new System.NotImplementedException();
	}

	public Task<T> GetAsync<T>(RestRequest request, CancellationToken cancellationToken)
	{
		throw new System.NotImplementedException();
	}

	public Task<HttpResponseMessage> GetAsync(RestRequest request, CancellationToken cancellationToken)
	{
		var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = ResponseContent };
		return Task.FromResult(response);
	}

	public T Post<T>(RestRequest postRequest)
	{
		return Task.Run(async () => await (PostAsync<T>(postRequest, CancellationToken.None)).ConfigureAwait(false)).Result;
	}

	public Task<T> PostAsync<T>(RestRequest postRequest, CancellationToken cancellationToken)
	{
		if (postRequest is SFRestRequest)
		{
			// authenticator
			var authnResponse = new AuthenticatorResponse
			{
				Success = true,
				Data = new AuthenticatorResponseData
				{
					tokenUrl = TokenUrl,
					ssoUrl = SSOUrl,
				}
			};

			return Task.FromResult<T>((T)(object)authnResponse);
		}
		else
		{
			//idp onetime token
			IdpTokenResponse tokenResponse = new IdpTokenResponse
			{
				CookieToken = "cookie",
			};
			return Task.FromResult<T>((T)(object)tokenResponse);
		}
	}

	public HttpResponseMessage Get(RestRequest request)
	{
		return Task.Run(async () => await (GetAsync(request, CancellationToken.None)).ConfigureAwait(false)).Result;
	}

	public void setHttpClient(HttpClient httpClient)
	{
		// Nothing to do
	}
}
