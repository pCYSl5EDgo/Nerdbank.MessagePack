﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft;

namespace Nerdbank.MessagePack;

public partial record MessagePackSerializer
{
	private static readonly StreamPipeWriterOptions PipeWriterOptions = new(MemoryPool<byte>.Shared, leaveOpen: true);
	private static readonly StreamPipeReaderOptions PipeReaderOptions = new(MemoryPool<byte>.Shared, leaveOpen: true);

	/// <summary>
	/// A thread-local, recyclable array that may be used for short bursts of code.
	/// </summary>
	[ThreadStatic]
	private static byte[]? scratchArray;

	/// <inheritdoc cref="Serialize{T}(ref MessagePackWriter, in T, ITypeShape{T})" />
	/// <returns>A byte array containing the serialized msgpack.</returns>
	public byte[] Serialize<T>(in T? value, ITypeShape<T> shape)
	{
		Requires.NotNull(shape);

		// Although the static array is thread-local, we still want to null it out while using it
		// to avoid any potential issues with re-entrancy due to a converter that makes a (bad) top-level call to the serializer.
		(byte[] array, scratchArray) = (scratchArray ?? new byte[65536], null);
		try
		{
			MessagePackWriter writer = new(SequencePool.Shared, array);
			this.Serialize(ref writer, value, shape);
			return writer.FlushAndGetArray();
		}
		finally
		{
			scratchArray = array;
		}
	}

	/// <inheritdoc cref="Serialize{T}(ref MessagePackWriter, in T, ITypeShape{T})"/>
	public void Serialize<T>(IBufferWriter<byte> writer, in T? value, ITypeShape<T> shape)
	{
		MessagePackWriter msgpackWriter = new(writer);
		this.Serialize(ref msgpackWriter, value, shape);
		msgpackWriter.Flush();
	}

	/// <inheritdoc cref="Serialize{T}(ref MessagePackWriter, in T, ITypeShape{T})"/>
	/// <param name="stream">The stream to write to.</param>
#pragma warning disable CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	public void Serialize<T>(Stream stream, in T? value, ITypeShape<T> shape)
#pragma warning restore CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	{
		Requires.NotNull(stream);
		this.Serialize(new StreamBufferWriter(stream), value, shape);
	}

	/// <summary>
	/// Serializes a value to a <see cref="Stream"/>.
	/// </summary>
	/// <typeparam name="T"><inheritdoc cref="SerializeAsync{T}(PipeWriter, T, ITypeShape{T}, CancellationToken)" path="/typeparam[@name='T']"/></typeparam>
	/// <param name="stream">The stream to write to.</param>
	/// <param name="value"><inheritdoc cref="SerializeAsync{T}(PipeWriter, T, ITypeShape{T}, CancellationToken)" path="/param[@name='value']"/></param>
	/// <param name="shape"><inheritdoc cref="SerializeAsync{T}(PipeWriter, T, ITypeShape{T}, CancellationToken)" path="/param[@name='shape']"/></param>
	/// <param name="cancellationToken"><inheritdoc cref="SerializeAsync{T}(PipeWriter, T, ITypeShape{T}, CancellationToken)" path="/param[@name='cancellationToken']"/></param>
	/// <returns><inheritdoc cref="SerializeAsync{T}(PipeWriter, T, ITypeShape{T}, CancellationToken)" path="/returns"/></returns>
#pragma warning disable CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	public async ValueTask SerializeAsync<T>(Stream stream, T? value, ITypeShape<T> shape, CancellationToken cancellationToken)
#pragma warning restore CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	{
		// Fast path for MemoryStream.
		if (stream is MemoryStream ms)
		{
			this.Serialize(stream, value, shape);
			return;
		}

		PipeWriter pipeWriter = PipeWriter.Create(stream, PipeWriterOptions);
		await this.SerializeAsync(pipeWriter, value, shape, cancellationToken).ConfigureAwait(false);
		await pipeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
		await pipeWriter.CompleteAsync().ConfigureAwait(false);
	}

	/// <inheritdoc cref="Deserialize{T}(in ReadOnlySequence{byte}, ITypeShape{T})"/>
	public T? Deserialize<T>(ReadOnlyMemory<byte> buffer, ITypeShape<T> shape)
	{
		MessagePackReader reader = new(buffer);
		return this.Deserialize(ref reader, shape);
	}

	/// <inheritdoc cref="Deserialize{T}(ref MessagePackReader, ITypeShape{T})"/>
	/// <param name="buffer">The msgpack to deserialize from.</param>
#pragma warning disable CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	public T? Deserialize<T>(scoped in ReadOnlySequence<byte> buffer, ITypeShape<T> shape)
#pragma warning restore CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	{
		MessagePackReader reader = new(buffer);
		return this.Deserialize(ref reader, shape);
	}

	/// <inheritdoc cref="Deserialize{T}(ref MessagePackReader, ITypeShape{T})"/>
	/// <param name="stream">The stream to deserialize from. If this stream contains more than one top-level msgpack structure, it may be positioned beyond its end after deserialization due to buffering.</param>
	/// <remarks>
	/// The implementation of this method currently is to buffer the entire content of the <paramref name="stream"/> into memory before deserializing.
	/// This is for simplicity and perf reasons.
	/// Callers should only provide streams that are known to be small enough to fit in memory and contain only msgpack content.
	/// </remarks>
#pragma warning disable CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	public T? Deserialize<T>(Stream stream, ITypeShape<T> shape)
#pragma warning restore CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	{
		Requires.NotNull(stream);

		// Fast path for MemoryStream.
		if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> buffer))
		{
			return this.Deserialize(buffer.AsMemory(), shape);
		}
		else
		{
			// We don't have a streaming msgpack reader, so buffer it all into memory instead and read from there.
			using SequencePool.Rental rental = SequencePool.Shared.Rent();
			int bytesLastRead;
			do
			{
				Span<byte> span = rental.Value.GetSpan(0);
				bytesLastRead = stream.Read(span);
				rental.Value.Advance(bytesLastRead);
			}
			while (bytesLastRead > 0);

			return this.Deserialize<T>(rental.Value, shape);
		}
	}

	/// <summary>
	/// Deserializes a value from a <see cref="Stream"/>.
	/// </summary>
	/// <typeparam name="T"><inheritdoc cref="SerializeAsync{T}(PipeWriter, T, ITypeShape{T}, CancellationToken)" path="/typeparam[@name='T']"/></typeparam>
	/// <param name="stream">The stream to deserialize from. If this stream contains more than one top-level msgpack structure, it may be positioned beyond its end after deserialization due to buffering.</param>
	/// <param name="shape"><inheritdoc cref="DeserializeAsync{T}(PipeReader, ITypeShape{T}, CancellationToken)" path="/param[@name='shape']"/></param>
	/// <param name="cancellationToken"><inheritdoc cref="DeserializeAsync{T}(PipeReader, ITypeShape{T}, CancellationToken)" path="/param[@name='cancellationToken']"/></param>
	/// <returns><inheritdoc cref="DeserializeAsync{T}(PipeReader, ITypeShape{T}, CancellationToken)" path="/returns"/></returns>
#pragma warning disable CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	public async ValueTask<T?> DeserializeAsync<T>(Stream stream, ITypeShape<T> shape, CancellationToken cancellationToken)
#pragma warning restore CS1573 // Parameter has no matching param tag in the XML comment (but other parameters do)
	{
		// Fast path for MemoryStream.
		if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> buffer))
		{
			return this.Deserialize(buffer.AsMemory(), shape);
		}

		PipeReader pipeReader = PipeReader.Create(stream, PipeReaderOptions);
		T? result = await this.DeserializeAsync(pipeReader, shape, cancellationToken).ConfigureAwait(false);
		await pipeReader.CompleteAsync().ConfigureAwait(false);
		return result;
	}
}