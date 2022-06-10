﻿namespace Tortuga.Data.Snowflake;

static class Utilities
{
	public static T? GetOptionOrDefault<T>(this HttpRequestMessage message, string key)
	{
#if NET5_0_OR_GREATER
        if (message.Options.TryGetValue<T>(new(key), out var value))
            return value;
        else
            return default(T?);
#else
		if (message.Properties.TryGetValue(key, out var value))
			return (T)value;
		else
			return default(T?);
#endif
	}

	public static void SetOption<T>(this HttpRequestMessage message, string key, T value)
	{
#if NET5_0_OR_GREATER
        message.Options.Set(new(key), value);
#else
		message.Properties[key] = value;
#endif
	}

	private static readonly TaskFactory _myTaskFactory = new TaskFactory(CancellationToken.None,
		TaskCreationOptions.None, TaskContinuationOptions.None, TaskScheduler.Default);
}
