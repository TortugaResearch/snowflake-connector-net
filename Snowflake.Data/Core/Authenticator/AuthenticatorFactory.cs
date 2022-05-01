﻿/*
 * Copyright (c) 2012-2021 Snowflake Computing Inc. All rights reserved.
 */

using Tortuga.Data.Snowflake.Core.Sessions;

namespace Tortuga.Data.Snowflake.Core.Authenticator;

/// <summary>
/// Authenticator Factory to build authenticators
/// </summary>
internal class AuthenticatorFactory
{
	/// <summary>
	/// Generate the authenticator given the session
	/// </summary>
	/// <param name="session">session that requires the authentication</param>
	/// <returns>authenticator</returns>
	/// <exception cref="SnowflakeDbException">when authenticator is unknown</exception>
	internal static IAuthenticator GetAuthenticator(SFSession session)
	{
		string type = session.properties[SFSessionProperty.AUTHENTICATOR];
		if (type.Equals(BasicAuthenticator.AUTH_NAME, StringComparison.InvariantCultureIgnoreCase))
		{
			return new BasicAuthenticator(session);
		}
		else if (type.Equals(ExternalBrowserAuthenticator.AUTH_NAME, StringComparison.InvariantCultureIgnoreCase))
		{
			return new ExternalBrowserAuthenticator(session);
		}
		else if (type.Equals(KeyPairAuthenticator.AUTH_NAME, StringComparison.InvariantCultureIgnoreCase))
		{
			// Get private key path or private key from connection settings
			if (!session.properties.TryGetValue(SFSessionProperty.PRIVATE_KEY_FILE, out var pkPath) &&
				!session.properties.TryGetValue(SFSessionProperty.PRIVATE_KEY, out var pkContent))
			{
				// There is no PRIVATE_KEY_FILE defined, can't authenticate with key-pair
				throw new SnowflakeDbException(SFError.INVALID_CONNECTION_STRING, "Missing required PRIVATE_KEY_FILE or PRIVATE_KEY for key pair authentication");
			}

			return new KeyPairAuthenticator(session);
		}
		else if (type.Equals(OAuthAuthenticator.AUTH_NAME, StringComparison.InvariantCultureIgnoreCase))
		{
			// Get private key path or private key from connection settings
			if (!session.properties.TryGetValue(SFSessionProperty.TOKEN, out var pkPath))
			{
				// There is no TOKEN defined, can't authenticate with oauth
				throw new SnowflakeDbException(SFError.INVALID_CONNECTION_STRING, "Missing required TOKEN for Oauth authentication");
			}

			return new OAuthAuthenticator(session);
		}
		// Okta would provide a url of form: https://xxxxxx.okta.com or https://xxxxxx.oktapreview.com or https://vanity.url/snowflake/okta
		else if (type.Contains("okta") && type.StartsWith("https://"))
		{
			return new OktaAuthenticator(session, type);
		}

		throw new SnowflakeDbException(SFError.UNKNOWN_AUTHENTICATOR, type);
	}
}