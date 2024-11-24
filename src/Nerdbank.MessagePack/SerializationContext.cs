﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Microsoft;

namespace Nerdbank.MessagePack;

/// <summary>
/// Context that flows through the serialization process.
/// </summary>
[DebuggerDisplay($"Depth remaining = {{{nameof(MaxDepth)}}}")]
public record struct SerializationContext
{
	/// <summary>
	/// Initializes a new instance of the <see cref="SerializationContext"/> struct.
	/// </summary>
	public SerializationContext()
	{
	}

	/// <summary>
	/// Gets or sets the remaining depth of the object graph to serialize or deserialize.
	/// </summary>
	/// <value>The default value is 64.</value>
	/// <remarks>
	/// Exceeding this depth will result in a <see cref="MessagePackSerializationException"/> being thrown
	/// from <see cref="DepthStep"/>.
	/// </remarks>
	public int MaxDepth { get; set; } = 64;

	/// <summary>
	/// Gets a hint as to the number of bytes to write into the buffer before serialization will flush the output.
	/// </summary>
	/// <value>The default value is 64KB.</value>
	public int UnflushedBytesThreshold { get; init; } = 64 * 1024;

	/// <summary>
	/// Gets the <see cref="MessagePackSerializer"/> that owns this context.
	/// </summary>
	internal MessagePackSerializer? Owner { get; private init; }

	/// <summary>
	/// Gets the reference equality tracker for this serialization operation.
	/// </summary>
	internal ReferenceEqualityTracker? ReferenceEqualityTracker { get; private init; }

	/// <summary>
	/// Decrements the depth remaining.
	/// </summary>
	/// <remarks>
	/// Converters that (de)serialize nested objects should invoke this once <em>before</em> passing the context to nested (de)serializers.
	/// </remarks>
	/// <exception cref="MessagePackSerializationException">Thrown if the depth limit has been exceeded.</exception>
	public void DepthStep()
	{
		if (--this.MaxDepth < 0)
		{
			throw new MessagePackSerializationException("Exceeded maximum depth of object graph.");
		}
	}

	/// <summary>
	/// Gets a converter for a specific type.
	/// </summary>
	/// <typeparam name="T">The type to be converted.</typeparam>
	/// <returns>The converter.</returns>
	/// <exception cref="InvalidOperationException">Thrown if no serialization operation is in progress.</exception>
	/// <remarks>
	/// This method is intended only for use by custom converters in order to delegate conversion of sub-values.
	/// </remarks>
	public MessagePackConverter<T> GetConverter<T>()
		where T : IShapeable<T>
	{
		Verify.Operation(this.Owner is not null, "No serialization operation is in progress.");
		MessagePackConverter<T> result = this.Owner.GetOrAddConverter(T.GetShape());
		return this.ReferenceEqualityTracker is null ? result : result.WrapWithReferencePreservation();
	}

	/// <summary>
	/// Gets a converter for a specific type.
	/// </summary>
	/// <typeparam name="T">The type to be converted.</typeparam>
	/// <typeparam name="TProvider">The type that provides the shape of the type to be converted.</typeparam>
	/// <returns>The converter.</returns>
	/// <exception cref="InvalidOperationException">Thrown if no serialization operation is in progress.</exception>
	/// <remarks>
	/// This method is intended only for use by custom converters in order to delegate conversion of sub-values.
	/// </remarks>
	public MessagePackConverter<T> GetConverter<T, TProvider>()
		where TProvider : IShapeable<T>
	{
		Verify.Operation(this.Owner is not null, "No serialization operation is in progress.");
		MessagePackConverter<T> result = this.Owner.GetOrAddConverter(TProvider.GetShape());
		return this.ReferenceEqualityTracker is null ? result : result.WrapWithReferencePreservation();
	}

	/// <summary>
	/// Starts a new serialization operation.
	/// </summary>
	/// <param name="owner">The owning serializer.</param>
	/// <returns>The new context for the operation.</returns>
	internal SerializationContext Start(MessagePackSerializer owner)
	{
		return this with
		{
			Owner = owner,
			ReferenceEqualityTracker = owner.PreserveReferences ? ReusableObjectPool<ReferenceEqualityTracker>.Take(owner) : null,
		};
	}

	/// <summary>
	/// Responds to the conclusion of a serialization operation by recycling any relevant objects.
	/// </summary>
	internal void End()
	{
		if (this.ReferenceEqualityTracker is not null)
		{
			ReusableObjectPool<ReferenceEqualityTracker>.Return(this.ReferenceEqualityTracker);
		}
	}
}
