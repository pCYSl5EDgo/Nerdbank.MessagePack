﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class ReflectionShapeProviderTests(ITestOutputHelper logger) : MessagePackSerializerTestBase(logger)
{
	[Fact]
	public void SerializeUnshapedType()
	{
		Person person = new("Andrew", "Arnott");
		ITypeShape<Person> shape = PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default.GetShape<Person>();
		byte[] msgpack = this.Serializer.Serialize(person, shape);
		this.LogMsgPack(new(msgpack));
		Person? deserialized = this.Serializer.Deserialize(msgpack, shape);
		Assert.Equal(person, deserialized);
	}

	public record Person(string FirstName, string LastName);
}