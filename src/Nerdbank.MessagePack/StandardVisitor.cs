﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;

namespace Nerdbank.MessagePack;

/// <summary>
/// A <see cref="TypeShapeVisitor"/> that produces <see cref="IMessagePackConverter{T}"/> instances for each type shape it visits.
/// </summary>
/// <param name="owner">The serializer that created this instance. Usable for obtaining settings that may influence the generated converter.</param>
internal class StandardVisitor(MessagePackSerializer owner) : TypeShapeVisitor
{
	private readonly TypeDictionary converters = new();

	/// <inheritdoc/>
	public override object? VisitObject<T>(IObjectTypeShape<T> objectShape, object? state = null)
	{
		IConstructorShape? ctorShape = objectShape.GetConstructor();

		bool? keyAttributesPresent = null;
		List<(ReadOnlyMemory<byte> RawPropertyNameString, SerializeProperty<T> Write)>? serializable = null;
		List<(ReadOnlyMemory<byte> PropertyNameUtf8, DeserializeProperty<T> Read)>? deserializable = null;
		List<(string Name, PropertyAccessors<T> Accessors)?>? propertyAccessors = null;
		foreach (IPropertyShape property in objectShape.GetProperties())
		{
			KeyAttribute? keyAttribute = (KeyAttribute?)property.AttributeProvider?.GetCustomAttributes(typeof(KeyAttribute), false).FirstOrDefault();
			if (keyAttributesPresent is null)
			{
				keyAttributesPresent = keyAttribute is not null;
			}
			else if (keyAttributesPresent != keyAttribute is not null)
			{
				throw new InvalidOperationException($"The type {objectShape.Type.FullName} has fields/properties that are candidates for serialization but are inconsistently attributed with {nameof(KeyAttribute)}.");
			}

			PropertyAccessors<T> accessors = (PropertyAccessors<T>)property.Accept(this)!;
			if (keyAttribute is not null)
			{
				propertyAccessors ??= new();
				while (propertyAccessors.Count <= keyAttribute.Index)
				{
					propertyAccessors.Add(null);
				}

				propertyAccessors[keyAttribute.Index] = (property.Name, accessors);
			}
			else
			{
				serializable ??= new();
				deserializable ??= new();

				CodeGenHelpers.GetEncodedStringBytes(property.Name, out ReadOnlyMemory<byte> utf8Bytes, out ReadOnlyMemory<byte> msgpackEncoded);
				if (accessors.Serialize is not null)
				{
					serializable.Add((msgpackEncoded, accessors.Serialize));
				}

				if (accessors.Deserialize is not null)
				{
					deserializable.Add((utf8Bytes, accessors.Deserialize));
				}
			}
		}

		if (propertyAccessors is not null)
		{
			ArrayConstructorVisitorInputs<T> inputs = new(propertyAccessors);
			return ctorShape is not null
				? ctorShape.Accept(this, inputs)
				: new ObjectArrayConverter<T>(inputs.GetJustAccessors(), null);
		}
		else
		{
			SpanDictionary<byte, DeserializeProperty<T>> propertyReaders = deserializable!
				.ToSpanDictionary(
					p => p.PropertyNameUtf8,
					p => p.Read,
					ByteSpanEqualityComparer.Ordinal);

			MapSerializableProperties<T> serializableMap = new(serializable ?? new());
			MapDeserializableProperties<T> deserializableMap = new(propertyReaders);
			MapConstructorVisitorInputs<T> inputs = new(serializableMap, deserializableMap);
			return ctorShape is not null
				? ctorShape.Accept(this, inputs)
				: new ObjectMapConverter<T>(serializableMap, null, null);
		}
	}

	/// <inheritdoc/>
	public override object? VisitProperty<TDeclaringType, TPropertyType>(IPropertyShape<TDeclaringType, TPropertyType> propertyShape, object? state = null)
	{
		IMessagePackConverter<TPropertyType> converter = this.GetConverter(propertyShape.PropertyType);

		SerializeProperty<TDeclaringType>? serialize = null;
		if (propertyShape.HasGetter)
		{
			Getter<TDeclaringType, TPropertyType> getter = propertyShape.GetGetter();
			serialize = (ref TDeclaringType container, ref MessagePackWriter writer, SerializationContext context) =>
			{
				TPropertyType? value = getter(ref container);
				converter.Serialize(ref writer, ref value, context);
			};
		}

		DeserializeProperty<TDeclaringType>? deserialize = null;
		if (propertyShape.HasSetter)
		{
			Setter<TDeclaringType, TPropertyType> setter = propertyShape.GetSetter();
			deserialize = (ref TDeclaringType container, ref MessagePackReader reader, SerializationContext context) => setter(ref container, converter.Deserialize(ref reader, context)!);
		}

		return new PropertyAccessors<TDeclaringType>(serialize, deserialize);
	}

	/// <inheritdoc/>
	public override object? VisitConstructor<TDeclaringType, TArgumentState>(IConstructorShape<TDeclaringType, TArgumentState> constructorShape, object? state = null)
	{
		switch (state)
		{
			case MapConstructorVisitorInputs<TDeclaringType> inputs:
				{
					if (constructorShape.ParameterCount == 0)
					{
						return new ObjectMapConverter<TDeclaringType>(inputs.Serializers, inputs.Deserializers, constructorShape.GetDefaultConstructor());
					}

					SpanDictionary<byte, DeserializeProperty<TArgumentState>> parameters = constructorShape.GetParameters()
						.Select(p => (p.Name, Deserialize: (DeserializeProperty<TArgumentState>)p.Accept(this)!))
						.ToSpanDictionary(
							p => Encoding.UTF8.GetBytes(p.Name),
							p => p.Deserialize,
							ByteSpanEqualityComparer.Ordinal);

					return new ObjectMapWithNonDefaultCtorConverter<TDeclaringType, TArgumentState>(
						inputs.Serializers,
						constructorShape.GetArgumentStateConstructor(),
						constructorShape.GetParameterizedConstructor(),
						new MapDeserializableProperties<TArgumentState>(parameters));
				}

			case ArrayConstructorVisitorInputs<TDeclaringType> inputs:
				{
					if (constructorShape.ParameterCount == 0)
					{
						return new ObjectArrayConverter<TDeclaringType>(inputs.GetJustAccessors(), constructorShape.GetDefaultConstructor());
					}

					Dictionary<string, int> propertyIndexesByName = new(StringComparer.Ordinal);
					for (int i = 0; i < inputs.Properties.Count; i++)
					{
						if (inputs.Properties[i] is { } property)
						{
							propertyIndexesByName[property.Name] = i;
						}
					}

					DeserializeProperty<TArgumentState>[] parameters = new DeserializeProperty<TArgumentState>[inputs.Properties.Count];
					foreach (IConstructorParameterShape parameter in constructorShape.GetParameters())
					{
						int index = propertyIndexesByName[parameter.Name];
						parameters[index] = (DeserializeProperty<TArgumentState>)parameter.Accept(this)!;
					}

					return new ObjectArrayWithNonDefaultCtorConverter<TDeclaringType, TArgumentState>(
						inputs.GetJustAccessors(),
						constructorShape.GetArgumentStateConstructor(),
						constructorShape.GetParameterizedConstructor(),
						parameters);
				}

			default:
				throw new NotSupportedException("Unsupported state.");
		}
	}

	/// <inheritdoc/>
	public override object? VisitConstructorParameter<TArgumentState, TParameterType>(IConstructorParameterShape<TArgumentState, TParameterType> parameterShape, object? state = null)
	{
		IMessagePackConverter<TParameterType> converter = owner.GetOrAddConverter(parameterShape.ParameterType);

		Setter<TArgumentState, TParameterType> setter = parameterShape.GetSetter();
		return new DeserializeProperty<TArgumentState>((ref TArgumentState state, ref MessagePackReader reader, SerializationContext context) => setter(ref state, converter.Deserialize(ref reader, context)!));
	}

	/// <inheritdoc/>
	public override object? VisitNullable<T>(INullableTypeShape<T> nullableShape, object? state = null) => new NullableConverter<T>(this.GetConverter(nullableShape.ElementType));

	/// <inheritdoc/>
	public override object? VisitDictionary<TDictionary, TKey, TValue>(IDictionaryShape<TDictionary, TKey, TValue> dictionaryShape, object? state = null)
	{
		// Serialization functions.
		IMessagePackConverter<TKey> keyConverter = this.GetConverter(dictionaryShape.KeyType);
		IMessagePackConverter<TValue> valueConverter = this.GetConverter(dictionaryShape.ValueType);
		Func<TDictionary, IReadOnlyDictionary<TKey, TValue>> getReadable = dictionaryShape.GetGetDictionary();

		// Deserialization functions.
		return dictionaryShape.ConstructionStrategy switch
		{
			CollectionConstructionStrategy.None => new DictionaryConverter<TDictionary, TKey, TValue>(getReadable, keyConverter, valueConverter),
			CollectionConstructionStrategy.Mutable => new MutableDictionaryConverter<TDictionary, TKey, TValue>(getReadable, keyConverter, valueConverter, dictionaryShape.GetAddKeyValuePair(), dictionaryShape.GetDefaultConstructor()),
			CollectionConstructionStrategy.Span => new ImmutableDictionaryConverter<TDictionary, TKey, TValue>(getReadable, keyConverter, valueConverter, dictionaryShape.GetSpanConstructor()),
			CollectionConstructionStrategy.Enumerable => new EnumerableDictionaryConverter<TDictionary, TKey, TValue>(getReadable, keyConverter, valueConverter, dictionaryShape.GetEnumerableConstructor()),
			_ => throw new NotSupportedException($"Unrecognized dictionary pattern: {typeof(TDictionary).Name}"),
		};
	}

	/// <inheritdoc/>
	public override object? VisitEnumerable<TEnumerable, TElement>(IEnumerableTypeShape<TEnumerable, TElement> enumerableShape, object? state = null)
	{
		IMessagePackConverter<TElement> elementConverter = this.GetConverter(enumerableShape.ElementType);

		if (enumerableShape.Type.IsArray)
		{
			return enumerableShape.Rank > 1
				? owner.MultiDimensionalArrayFormat switch
				{
					MultiDimensionalArrayFormat.Nested => new ArrayWithNestedDimensionsConverter<TEnumerable, TElement>(elementConverter, enumerableShape.Rank),
					MultiDimensionalArrayFormat.Flat => new ArrayWithFlattenedDimensionsConverter<TEnumerable, TElement>(elementConverter),
					_ => throw new NotSupportedException(),
				}
				: new ArrayConverter<TElement>(elementConverter);
		}

		return enumerableShape.ConstructionStrategy switch
		{
			CollectionConstructionStrategy.None => new EnumerableConverter<TEnumerable, TElement>(enumerableShape.GetGetEnumerable(), elementConverter),
			CollectionConstructionStrategy.Mutable => new MutableEnumerableConverter<TEnumerable, TElement>(enumerableShape.GetGetEnumerable(), elementConverter, enumerableShape.GetAddElement(), enumerableShape.GetDefaultConstructor()),
			CollectionConstructionStrategy.Span => new SpanEnumerableConverter<TEnumerable, TElement>(enumerableShape.GetGetEnumerable(), elementConverter, enumerableShape.GetSpanConstructor()),
			CollectionConstructionStrategy.Enumerable => new EnumerableEnumerableConverter<TEnumerable, TElement>(enumerableShape.GetGetEnumerable(), elementConverter, enumerableShape.GetEnumerableConstructor()),
			_ => throw new NotSupportedException($"Unrecognized enumerable pattern: {typeof(TEnumerable).Name}"),
		};
	}

	/// <inheritdoc/>
	public override object? VisitEnum<TEnum, TUnderlying>(IEnumTypeShape<TEnum, TUnderlying> enumShape, object? state = null)
		=> new EnumAsOrdinalConverter<TEnum, TUnderlying>(this.GetConverter(enumShape.UnderlyingType));

	/// <summary>
	/// Gets or creates a converter for the given type shape.
	/// </summary>
	/// <typeparam name="T">The data type to make convertible.</typeparam>
	/// <param name="shape">The type shape.</param>
	/// <returns>The converter.</returns>
	protected IMessagePackConverter<T> GetConverter<T>(ITypeShape<T> shape)
	{
		if (owner.TryGetConverter(out IMessagePackConverter<T>? converter))
		{
			return converter;
		}

		return this.converters.GetOrAdd<IMessagePackConverter<T>>(shape, this, box => new DelayedConverter<T>(box));
	}
}
