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
