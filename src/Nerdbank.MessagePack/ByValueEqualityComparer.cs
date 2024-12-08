﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack.SecureHash;
using PolyType.Utilities;

namespace Nerdbank.MessagePack;

/// <summary>
/// A non-generic class for caching equality comparers.
/// </summary>
internal static class ByValueEqualityComparer
{
	/// <summary>
	/// Cache for generated by-value comparers.
	/// </summary>
	internal static readonly MultiProviderTypeCache DefaultEqualityComparerCache = new()
	{
		DelayedValueFactory = new ByValueVisitor.DelayedEqualityComparerFactory(),
		ValueBuilderFactory = ctx => new ByValueVisitor(ctx),
	};

	/// <summary>
	/// Cache for generated secure by-value comparers.
	/// </summary>
	internal static readonly MultiProviderTypeCache HashResistantEqualityComparerCache = new()
	{
		DelayedValueFactory = new SecureVisitor.DelayedEqualityComparerFactory(),
		ValueBuilderFactory = ctx => new SecureVisitor(ctx),
	};
}
