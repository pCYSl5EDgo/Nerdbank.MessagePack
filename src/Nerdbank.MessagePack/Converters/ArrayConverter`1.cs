﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;

namespace Nerdbank.MessagePack.Converters;

/// <summary>
/// Serializes and deserializes a 1-rank array.
/// </summary>
/// <typeparam name="TElement">The element type.</typeparam>
internal class ArrayConverter<TElement>(MessagePackConverter<TElement> elementConverter) : MessagePackConverter<TElement[]>
{
	/// <inheritdoc/>
	public override bool PreferAsyncSerialization => true;

	/// <inheritdoc/>
	public override TElement[]? Read(ref MessagePackReader reader, SerializationContext context)
	{
		if (reader.TryReadNil())
		{
			return null;
		}

		context.DepthStep();
		int count = reader.ReadArrayHeader();
		TElement[] array = new TElement[count];
		for (int i = 0; i < count; i++)
		{
			array[i] = elementConverter.Read(ref reader, context)!;
		}

		return array;
	}

	/// <inheritdoc/>
	public override void Write(ref MessagePackWriter writer, in TElement[]? value, SerializationContext context)
	{
		if (value is null)
		{
			writer.WriteNil();
			return;
		}

		context.DepthStep();
		writer.WriteArrayHeader(value.Length);
		for (int i = 0; i < value.Length; i++)
		{
			elementConverter.Write(ref writer, value[i], context);
		}
	}

	/// <inheritdoc/>
	[Experimental("NBMsgPackAsync")]
	public override async ValueTask WriteAsync(MessagePackAsyncWriter writer, TElement[]? value, SerializationContext context, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (value is null)
		{
			writer.WriteNil();
			return;
		}

		context.DepthStep();
		if (elementConverter.PreferAsyncSerialization)
		{
			writer.WriteArrayHeader(value.Length);
			for (int i = 0; i < value.Length; i++)
			{
				await elementConverter.WriteAsync(writer, value[i], context, cancellationToken).ConfigureAwait(false);
				await writer.FlushIfAppropriateAsync(context, cancellationToken).ConfigureAwait(false);
			}
		}
		else
		{
			int progress = 0;
			do
			{
				MessagePackWriter syncWriter = writer.CreateWriter();
				syncWriter.WriteArrayHeader(value.Length);
				for (; progress < value.Length && !writer.IsTimeToFlush(context, syncWriter); progress++)
				{
					elementConverter.Write(ref syncWriter, value[progress], context);
					cancellationToken.ThrowIfCancellationRequested();
				}

				syncWriter.Flush();
				await writer.FlushIfAppropriateAsync(context, cancellationToken).ConfigureAwait(false);
			}
			while (progress < value.Length);
		}
	}

	/// <inheritdoc/>
	[Experimental("NBMsgPackAsync")]
	public override async ValueTask<TElement[]?> ReadAsync(MessagePackAsyncReader reader, SerializationContext context, CancellationToken cancellationToken)
	{
		if (await reader.TryReadNilAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		context.DepthStep();

		if (elementConverter.PreferAsyncSerialization)
		{
			int count = await reader.ReadArrayHeaderAsync(cancellationToken).ConfigureAwait(false);
			TElement[] array = new TElement[count];
			for (int i = 0; i < count; i++)
			{
				array[i] = (await elementConverter.ReadAsync(reader, context, cancellationToken).ConfigureAwait(false))!;
			}

			return array;
		}
		else
		{
			ReadOnlySequence<byte> map = await reader.ReadNextStructureAsync(context, cancellationToken).ConfigureAwait(false);

			MessagePackReader syncReader = new(map);
			int count = syncReader.ReadArrayHeader();
			TElement[] array = new TElement[count];
			for (int i = 0; i < count; i++)
			{
				array[i] = elementConverter.Read(ref syncReader, context)!;
			}

			reader.AdvanceTo(map.End);
			return array;
		}
	}
}
