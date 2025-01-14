﻿/*
 * Copyright (c) 2012-2021 Snowflake Computing Inc. All rights reserved.
 */

using System.Net;
using System.Security;
using Tortuga.Data.Snowflake.Core.Authenticators;
using Tortuga.Data.Snowflake.Legacy;

namespace Tortuga.Data.Snowflake.Core.Sessions;

#pragma warning disable CS0659 // Type overrides Object.Equals(object o) but does not override Object.GetHashCode()

class SFSessionProperties : Dictionary<SFSessionProperty, string>
#pragma warning restore CS0659 // Type overrides Object.Equals(object o) but does not override Object.GetHashCode()
{
	public override bool Equals(object? obj)
	{
		if (obj == null)
			return false;

		try
		{
			var prop = (SFSessionProperties)obj;
#pragma warning disable CS8605 // Unboxing a possibly null value. Workaround for nullability bug in .NET Core 3.1
			foreach (SFSessionProperty sessionProperty in Enum.GetValues(typeof(SFSessionProperty)))
			{
				if (ContainsKey(sessionProperty) ^ prop.ContainsKey(sessionProperty))
					return false;

				if (!ContainsKey(sessionProperty))
					continue;

				if (!this[sessionProperty].Equals(prop[sessionProperty], StringComparison.Ordinal))
					return false;
			}
#pragma warning restore CS8605 // Unboxing a possibly null value.
			return true;
		}
		catch (InvalidCastException)
		{
			return false;
		}
	}

	internal static SFSessionProperties parseConnectionString(string connectionString, SecureString? password)
	{
		var properties = new SFSessionProperties();

		var propertyEntry = connectionString.Split(';');

		foreach (var keyVal in propertyEntry)
		{
			if (keyVal.Length > 0)
			{
				var tokens = keyVal.Split(new string[] { "=" }, StringSplitOptions.None);
				if (tokens.Length != 2)
				{
					// https://docs.microsoft.com/en-us/dotnet/api/system.data.oledb.oledbconnection.connectionstring
					// To include an equal sign (=) in a keyword or value, it must be preceded
					// by another equal sign. For example, in the hypothetical connection
					// string "key==word=value" :
					// the keyword is "key=word" and the value is "value".
					var currentIndex = 0;
					var singleEqualIndex = -1;
					while (currentIndex <= keyVal.Length)
					{
						currentIndex = keyVal.IndexOf("=", currentIndex, StringComparison.Ordinal);
						if (-1 == currentIndex)
						{
							// No '=' found
							break;
						}
						if (currentIndex < keyVal.Length - 1 &&
							'=' != keyVal[currentIndex + 1])
						{
							if (0 > singleEqualIndex)
							{
								// First single '=' encountered
								singleEqualIndex = currentIndex;
								currentIndex++;
							}
							else
							{
								// Found another single '=' which is not allowed
								singleEqualIndex = -1;
								break;
							}
						}
						else
						{
							// skip the doubled one
							currentIndex += 2;
						}
					}

					if (singleEqualIndex > 0 && singleEqualIndex < keyVal.Length - 1)
					{
						// Split the key/value at the right index and deduplicate '=='
						tokens = new string[2];
						tokens[0] = keyVal.Substring(0, singleEqualIndex).Replace("==", "=", StringComparison.Ordinal);
						tokens[1] = keyVal.Substring(singleEqualIndex + 1, keyVal.Length - (singleEqualIndex + 1)).Replace("==", "=", StringComparison.Ordinal); ;
					}
					else
					{
						// An equal sign was not doubled or something else happened
						// making the connection invalid
						var invalidStringDetail = $"Invalid key value pair {keyVal}";
						throw new SnowflakeDbException(SnowflakeDbError.InvalidConnectionString, new object[] { invalidStringDetail });
					}
				}

				try
				{
					var p = (SFSessionProperty)Enum.Parse(typeof(SFSessionProperty), tokens[0].ToUpperInvariant());
					properties.Add(p, tokens[1]);
				}
				catch (ArgumentException)
				{
					//Property not found, ignored
				}
			}
		}

		var useProxy = false;
		if (properties.ContainsKey(SFSessionProperty.USEPROXY))
		{
			try
			{
				useProxy = bool.Parse(properties[SFSessionProperty.USEPROXY]);
			}
			catch (Exception e)
			{
				// The useProxy setting is not a valid boolean value
				throw new SnowflakeDbException(e, SnowflakeDbError.InvalidConnectionString, e.Message);
			}
		}

		if (password != null)
		{
			properties[SFSessionProperty.PASSWORD] = new NetworkCredential(string.Empty, password).Password;
		}

		CheckSessionProperties(properties, useProxy);

		// compose host value if not specified
		if (!properties.ContainsKey(SFSessionProperty.HOST) || 0 == properties[SFSessionProperty.HOST].Length)
		{
			var hostName = $"{properties[SFSessionProperty.ACCOUNT]}.snowflakecomputing.com";
			// Remove in case it's here but empty
			properties.Remove(SFSessionProperty.HOST);
			properties.Add(SFSessionProperty.HOST, hostName);
		}

		// Trim the account name to remove the region and cloud platform if any were provided
		// because the login request data does not expect region and cloud information to be
		// passed on for account_name
		properties[SFSessionProperty.ACCOUNT] = properties[SFSessionProperty.ACCOUNT].Split('.')[0];

		return properties;
	}

	private static void CheckSessionProperties(SFSessionProperties properties, bool useProxy)
	{
#pragma warning disable CS8605 // Unboxing a possibly null value. Workaround for nullability bug in .NET Core 3.1
		foreach (SFSessionProperty sessionProperty in Enum.GetValues(typeof(SFSessionProperty)))
		{
			var isRequired = IsRequired(sessionProperty, properties);
			if (useProxy)
			{
				// If useProxy is true, then proxyhost and proxy port are mandatory
				if (sessionProperty is (SFSessionProperty.PROXYHOST or SFSessionProperty.PROXYPORT))
					isRequired = true;

				// If a username is provided, then a password is required
				if (sessionProperty == SFSessionProperty.PROXYPASSWORD && properties.ContainsKey(SFSessionProperty.PROXYUSER))
					isRequired = true;
			}

			// if required property, check if exists in the dictionary
			if (isRequired && !properties.ContainsKey(sessionProperty))
			{
				throw new SnowflakeDbException(SnowflakeDbError.MissingConnectionProperty, sessionProperty);
			}

			// add default value to the map
			var defaultVal = sessionProperty.GetAttribute<SFSessionPropertyAttribute>()?.DefaultValue;
			if (defaultVal != null && !properties.ContainsKey(sessionProperty))
				properties.Add(sessionProperty, defaultVal);
		}
#pragma warning restore CS8605 // Unboxing a possibly null value.
	}

	private static bool IsRequired(SFSessionProperty sessionProperty, SFSessionProperties properties)
	{
		if (sessionProperty.Equals(SFSessionProperty.PASSWORD))
		{
			if (!properties.TryGetValue(SFSessionProperty.AUTHENTICATOR, out var authenticator))
				return true;

			// External browser, jwt and OAuth don't require a password for authenticating
			return !(authenticator.Equals(ExternalBrowserAuthenticator.AUTH_NAME, StringComparison.OrdinalIgnoreCase) ||
					authenticator.Equals(KeyPairAuthenticator.AUTH_NAME, StringComparison.OrdinalIgnoreCase) ||
					authenticator.Equals(OAuthAuthenticator.AUTH_NAME, StringComparison.OrdinalIgnoreCase));
		}
		else if (sessionProperty.Equals(SFSessionProperty.USER))
		{
			if (!properties.TryGetValue(SFSessionProperty.AUTHENTICATOR, out var authenticator))
				return false;

			// OAuth don't require a username for authenticating
			return !authenticator.Equals(OAuthAuthenticator.AUTH_NAME, StringComparison.OrdinalIgnoreCase);
		}
		else
		{
			return sessionProperty.GetAttribute<SFSessionPropertyAttribute>()?.Required ?? false;
		}
	}
}
