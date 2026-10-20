using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CX.Framework;

/// <summary>A read-only GFF V3.2 file (BioWare's Generic File Format: <c>.git</c>, <c>.are</c>, <c>.ifo</c>, ...). Reads
/// scalars, strings, resrefs, structs and lists; every offset is bounds-checked, so a bad file throws <see cref="FormatException"/>.</summary>
public readonly struct Gff
{
    private const int HeaderBytes = 56;
    private const int StructBytes = 12, FieldBytes = 12, LabelBytes = 16;
    /// <summary>Header words: six (offset, count) pairs after the type and version, in file order.</summary>
    private const int HStructs = 8, HFields = 16, HLabels = 24, HData = 32, HFieldIdx = 40, HListIdx = 48;

    /// <summary>GFF field types this reader names.</summary>
    public enum FieldType : uint
    {
        Byte = 0, Char = 1, Word = 2, Short = 3, Dword = 4, Int = 5, Dword64 = 6, Int64 = 7, Float = 8, Double = 9,
        ExoString = 10, ResRef = 11, ExoLocString = 12, Void = 13, Struct = 14, List = 15,
    }

    private readonly byte[] _b;
    private readonly int _structs, _structCount, _fields, _fieldCount, _labels, _labelCount, _data, _dataLen, _fieldIdx, _fieldIdxLen, _listIdx, _listIdxLen;

    private Gff(byte[] b)
    {
        _b = b;
        if (b.Length < HeaderBytes || Encoding.ASCII.GetString(b, 4, 4) != "V3.2") throw new FormatException("not a GFF V3.2 file");
        _structs = I(HStructs); _structCount = I(HStructs + 4); _fields = I(HFields); _fieldCount = I(HFields + 4);
        _labels = I(HLabels); _labelCount = I(HLabels + 4); _data = I(HData); _dataLen = I(HData + 4);
        _fieldIdx = I(HFieldIdx); _fieldIdxLen = I(HFieldIdx + 4); _listIdx = I(HListIdx); _listIdxLen = I(HListIdx + 4);
        Span(_structs, (long)_structCount * StructBytes); Span(_fields, (long)_fieldCount * FieldBytes);
        Span(_labels, (long)_labelCount * LabelBytes); Span(_data, _dataLen); Span(_fieldIdx, _fieldIdxLen); Span(_listIdx, _listIdxLen);
    }

    /// <summary>Parse <paramref name="bytes"/>; null, with <paramref name="why"/>, when it is no readable GFF.</summary>
    public static Gff? TryParse(byte[] bytes, out string? why)
    {
        why = null;
        try { return new Gff(bytes); }
        catch (FormatException e) { why = e.Message; return null; }
    }

    /// <summary>The four-character file type, <c>"GIT "</c> for a <c>.git</c>.</summary>
    public string FileType => Encoding.ASCII.GetString(_b, 0, 4);

    /// <summary>The top-level struct.</summary>
    public GffStruct Top => Struct(0);

    internal GffStruct Struct(uint index)
    {
        if (index >= (uint)_structCount) throw new FormatException($"struct {index} past {_structCount}");
        return new GffStruct(this, _structs + (int)index * StructBytes);
    }

    private int I(int at) => U(at) is var v && v <= int.MaxValue ? (int)v : throw new FormatException($"header word at {at} is {v}");
    internal uint U(int at)
    {
        if ((uint)at > (uint)(_b.Length - 4)) throw new FormatException($"read at {at} past the file");
        return BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(at));
    }

    internal ReadOnlySpan<byte> Span(long at, long len)
    {
        if (at < 0 || len < 0 || at + len > _b.Length) throw new FormatException($"block {at}+{len} past the file");
        return _b.AsSpan((int)at, (int)len);
    }

    /// <summary>Field <paramref name="i"/>'s (type, label, data-or-offset).</summary>
    internal (FieldType Type, string Label, uint Data) Field(uint i)
    {
        if (i >= (uint)_fieldCount) throw new FormatException($"field {i} past {_fieldCount}");
        int at = _fields + (int)i * FieldBytes;
        uint label = U(at + 4);
        if (label >= (uint)_labelCount) throw new FormatException($"label {label} past {_labelCount}");
        return ((FieldType)U(at), Native.FixedText(Span(_labels + (long)label * LabelBytes, LabelBytes)), U(at + 8));
    }

    internal uint FieldIndex(uint byteOffset, int k)
    {
        if ((long)byteOffset + 4L * (k + 1) > _fieldIdxLen) throw new FormatException("field index past its block");
        return U(_fieldIdx + (int)byteOffset + 4 * k);
    }

    internal ReadOnlySpan<byte> Data(uint offset, int len)
    {
        if ((long)offset + len > _dataLen) throw new FormatException("field data past its block");
        return Span(_data + offset, len);
    }

    internal uint List(uint byteOffset, int k)
    {
        if ((long)byteOffset + 4L * (k + 1) > _listIdxLen) throw new FormatException("list index past its block");
        return U(_listIdx + (int)byteOffset + 4 * k);
    }
}

/// <summary>One struct of a <see cref="Gff"/>: its fields by label.</summary>
public readonly struct GffStruct
{
    private readonly Gff _g;
    private readonly int _at;

    internal GffStruct(Gff g, int at) { _g = g; _at = at; }

    /// <summary>The struct's type id (<c>0xFFFFFFFF</c> for the top struct).</summary>
    public uint Type => _g.U(_at);

    /// <summary>Every field, in file order.</summary>
    public IEnumerable<GffField> Fields
    {
        get
        {
            uint n = _g.U(_at + 8), d = _g.U(_at + 4);
            var all = new List<GffField>((int)Math.Min(n, 4096u));
            for (int k = 0; k < n; k++)
            {
                var f = _g.Field(n == 1 ? d : _g.FieldIndex(d, k));
                all.Add(new GffField(_g, f.Type, f.Label, f.Data));
            }
            return all;
        }
    }

    /// <summary>The field labelled <paramref name="label"/> (labels are case-sensitive), or null.</summary>
    public GffField? Get(string label)
    {
        foreach (GffField f in Fields) if (f.Label == label) return f;
        return null;
    }
}

/// <summary>One field: its type, label and value.</summary>
public readonly struct GffField
{
    private readonly Gff _g;
    private readonly uint _d;
    public Gff.FieldType Type { get; }
    public string Label { get; }

    internal GffField(Gff g, Gff.FieldType type, string label, uint data) { _g = g; Type = type; Label = label; _d = data; }

    /// <summary>A whole-number field (BYTE..INT64) as a long; null for any other type.</summary>
    public long? Integer => Type switch
    {
        Gff.FieldType.Byte => (byte)_d, Gff.FieldType.Char => (sbyte)(byte)_d, Gff.FieldType.Word => (ushort)_d,
        Gff.FieldType.Short => (short)(ushort)_d, Gff.FieldType.Dword => _d, Gff.FieldType.Int => (int)_d,
        Gff.FieldType.Dword64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(_g.Data(_d, 8)),
        Gff.FieldType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(_g.Data(_d, 8)),
        _ => null,
    };

    /// <summary>A FLOAT or DOUBLE field; null for any other type.</summary>
    public double? Real => Type switch
    {
        Gff.FieldType.Float => BitConverter.Int32BitsToSingle((int)_d),
        Gff.FieldType.Double => BinaryPrimitives.ReadDoubleLittleEndian(_g.Data(_d, 8)),
        _ => null,
    };

    /// <summary>A CExoString or ResRef field's text (single-byte, as the engine stores it); null for any other type.</summary>
    public string? Text
    {
        get
        {
            if (Type == Gff.FieldType.ExoString)
            {
                uint n = BinaryPrimitives.ReadUInt32LittleEndian(_g.Data(_d, 4));
                if (n > int.MaxValue) throw new FormatException($"string of {n} bytes");
                return Encoding.Latin1.GetString(_g.Data(_d + 4, (int)n));
            }
            if (Type == Gff.FieldType.ResRef) return Encoding.Latin1.GetString(_g.Data(_d + 1, _g.Data(_d, 1)[0]));
            return null;
        }
    }

    /// <summary>A scalar or text field as a knob value (<c>1</c>, <c>0.25</c>, <c>low</c>); null for a struct, list,
    /// localized string or void.</summary>
    public string? AsValue => Integer is long i ? i.ToString(CultureInfo.InvariantCulture)
                            : Real is double r ? ((float)r).ToString("R", CultureInfo.InvariantCulture)
                            : Text;

    /// <summary>A STRUCT field's struct; null for any other type.</summary>
    public GffStruct? Struct => Type == Gff.FieldType.Struct ? _g.Struct(_d) : null;

    /// <summary>A LIST field's structs; empty for any other type.</summary>
    public IEnumerable<GffStruct> List
    {
        get
        {
            if (Type != Gff.FieldType.List) return Array.Empty<GffStruct>();
            uint n = _g.List(_d, 0);
            var all = new List<GffStruct>((int)Math.Min(n, 4096u));
            for (int k = 0; k < n; k++) all.Add(_g.Struct(_g.List(_d, k + 1)));
            return all;
        }
    }
}
