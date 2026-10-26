// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The schema-path column every catalog discovery stream leads with since ABI v97 (tables and schemas since v90):
/// a <c>LIST&lt;VARCHAR&gt;</c>, outermost segment first.
/// </summary>
internal sealed class SchemaPathColumn
{
    private readonly ListArray.Builder _builder = new(StringType.Default);

    public static Field Field(string name) =>
        new(name, new ListType(new Field("item", StringType.Default, nullable: true)), nullable: true);

    public void Append(SchemaPath path)
    {
        _builder.Append();
        var segments = (StringArray.Builder)_builder.ValueBuilder;
        foreach (var segment in path)
        {
            segments.Append(segment);
        }
    }

    public ListArray Build() => _builder.Build();
}
