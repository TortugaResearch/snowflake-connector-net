﻿/*
 * Copyright (c) 2021 Snowflake Computing Inc. All rights reserved.
 */



namespace Tortuga.Data.Snowflake.Tests.Mock;

class MockSecretDetector
{
	public static SecretDetector.Mask MaskSecrets(string text)
	{
		SecretDetector.Mask result = new SecretDetector.Mask();
		try
		{
			throw new Exception("Test exception");
		}
		catch (Exception ex)
		{
			//We'll assume that the exception was raised during masking
			//to be safe consider that the log has sensitive information
			//and do not raise an exception.
			result.IsMasked = true;
			result.MaskedText = ex.Message;
			result.ErrStr = ex.Message;
		}
		return result;
	}
}
