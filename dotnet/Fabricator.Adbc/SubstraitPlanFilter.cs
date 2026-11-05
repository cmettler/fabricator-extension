// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Adbc;

/// <summary>
/// Puts a filter around a pushed plan: the root's input becomes <c>FilterRel(input, condition)</c>, and the
/// condition's functions are declared beside the plan's own. Everything else in the plan is copied as it is.
/// </summary>
internal static class SubstraitPlanFilter
{
    // Plan fields
    private const int PlanExtensionUris = 1;
    private const int PlanExtensions = 2;
    private const int PlanRelations = 3;
    private const int PlanExtensionUrns = 8;

    /// <summary>The type URLs of the plan's optimization hints (<c>advanced_extensions.optimization</c>).</summary>
    public static HashSet<string> Optimizations(byte[] plan)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var reader = new ProtoReader(plan);
        while (reader.Next(out int field, out int wire, out _))
        {
            if (field != 4 || wire != 2) // Plan.advanced_extensions
            {
                reader.Skip(wire);
                continue;
            }
            var extension = reader.Message();
            while (extension.Next(out int ef, out int ew, out _))
            {
                if (ef != 1 || ew != 2) // AdvancedExtension.optimization (google.protobuf.Any)
                {
                    extension.Skip(ew);
                    continue;
                }
                var any = extension.Message();
                while (any.Next(out int af, out int aw, out _))
                {
                    if (af == 1 && aw == 2) // Any.type_url
                        result.Add(System.Text.Encoding.UTF8.GetString(any.Payload().ToArray()));
                    else
                        any.Skip(aw);
                }
            }
        }
        return result;
    }

    /// <summary>A function registry whose anchors start above every anchor <paramref name="plan"/> uses.</summary>
    public static SubstraitFunctions FunctionsFor(byte[] plan)
    {
        uint extensions = 0, functions = 0;
        var reader = new ProtoReader(plan);
        while (reader.Next(out int field, out int wire, out _))
        {
            if (wire != 2)
            {
                reader.Skip(wire);
                continue;
            }
            switch (field)
            {
                case PlanExtensionUris:
                case PlanExtensionUrns:
                    extensions = Math.Max(extensions, Anchor(reader.Message(), 1));
                    break;
                case PlanExtensions:
                {
                    var declaration = reader.Message();
                    while (declaration.Next(out int kind, out int kindWire, out _))
                    {
                        if (kind == 3 && kindWire == 2) // extension_function: its function_anchor
                            functions = Math.Max(functions, Anchor(declaration.Message(), 2));
                        else
                            declaration.Skip(kindWire);
                    }
                    break;
                }
                default:
                    reader.Skip(wire);
                    break;
            }
        }
        return new SubstraitFunctions(extensions + 1, functions + 1);
    }

    /// <summary><paramref name="plan"/> with its root's input filtered by <paramref name="condition"/>, which refers
    /// to that input's columns by position.</summary>
    public static byte[] Wrap(byte[] plan, ProtoWriter condition, SubstraitFunctions functions)
    {
        var result = new ProtoWriter();
        bool wrapped = false;
        var reader = new ProtoReader(plan);
        while (reader.Next(out int field, out int wire, out int start))
        {
            if (field == PlanRelations && wire == 2 && !wrapped)
            {
                var relation = reader.Message();
                var rewritten = new ProtoWriter();
                while (relation.Next(out int relField, out int relWire, out int relStart))
                {
                    if (relField == 2 && relWire == 2) // PlanRel.root
                    {
                        rewritten.Message(2, WrapRoot(relation.Message(), condition));
                        wrapped = true;
                    }
                    else
                    {
                        relation.Skip(relWire);
                        rewritten.Raw(relation.Encoded(relStart));
                    }
                }
                result.Message(PlanRelations, rewritten);
                continue;
            }
            reader.Skip(wire);
            result.Raw(reader.Encoded(start));
        }
        if (!wrapped)
            throw new InvalidDataException("adbc: the pushed plan has no root relation");
        functions.WriteTo(result);
        return result.ToArray();
    }

    private static ProtoWriter WrapRoot(ProtoReader root, ProtoWriter condition)
    {
        var rewritten = new ProtoWriter();
        while (root.Next(out int field, out int wire, out int start))
        {
            if (field == 1 && wire == 2) // RelRoot.input
            {
                var input = root.Payload();
                var filter = new ProtoWriter().Bytes(2, input).Message(3, condition); // FilterRel.input, condition
                rewritten.Message(1, new ProtoWriter().Message(2, filter)); // Rel.filter
            }
            else
            {
                root.Skip(wire);
                rewritten.Raw(root.Encoded(start));
            }
        }
        return rewritten;
    }

    private static uint Anchor(ProtoReader message, int anchorField)
    {
        uint anchor = 0;
        while (message.Next(out int field, out int wire, out _))
        {
            if (field == anchorField && wire == 0)
                anchor = (uint)message.ReadVarint();
            else
                message.Skip(wire);
        }
        return anchor;
    }
}
