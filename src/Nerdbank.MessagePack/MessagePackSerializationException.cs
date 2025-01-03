﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;

namespace Nerdbank.MessagePack;

/// <summary>
/// Represents errors that occur during MessagePack serialization.
/// </summary>
public class MessagePackSerializationException : Exception
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MessagePackSerializationException"/> class.
	/// </summary>
	public MessagePackSerializationException()
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="MessagePackSerializationException"/> class with a specified error message.
	/// </summary>
	/// <param name="message">The message that describes the error.</param>
	public MessagePackSerializationException(string? message)
		: base(message)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="MessagePackSerializationException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
	/// </summary>
	/// <param name="message">The message that describes the error.</param>
	/// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
	public MessagePackSerializationException(string? message, Exception? innerException)
		: base(message, innerException)
	{
	}

	/// <summary>
	/// Throws an exception explaining that nil was unexpectedly encountered while deserializing a value type.
	/// </summary>
	/// <typeparam name="T">The value type that was being deserialized.</typeparam>
	/// <returns>Nothing. This method always throws.</returns>
	[DoesNotReturn]
	internal static MessagePackSerializationException ThrowUnexpectedNilWhileDeserializing<T>() => throw new MessagePackSerializationException("Unexpected nil encountered while deserializing " + typeof(T).FullName);
}
