namespace CX.Framework;

/// <summary>The rows of <see cref="EngineLayout"/> an effect's own visual transform needs: the transform the
/// effect was added with, and the C++ runtime layout a fingerprint of its contents reads.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>CNWCVisualEffectOnObject -&gt; the <c>ObjectVisualTransformData</c> the effect carries, embedded in
    /// the effect at the same place on both builds: <c>LoadVisualEffect</c> assigns the incoming transform over it
    /// (Linux @0x819116, Windows rva 0x96ebf5).</summary>
    public static readonly int EffectTransform =
        Field<CX.Engine.ObjectVisualTransformData>("CNWCVisualEffectOnObject", "transform", embedded: true);

    // An ObjectVisualTransformData IS a std::map<int, CAurObjectVisualTransformData>, and the two runtimes lay the
    // map out differently: libstdc++ embeds the header node in it, MSVC keeps a pointer to one. Hence the one row
    // that does not pair -- tree.head is where the header node starts on Linux and where its pointer sits on
    // Windows. Everything below pairs at delta 0. Read off CNWCObject::AddVisualEffect's copy of the caller's map
    // (Linux @0x7e9e9c), ObjectVisualTransformData::Read (Linux @0x8e3990) and the Windows assignment inlined in
    // LoadVisualEffect (rva 0x96ebfc), whose MSVC tree walk tests a node's nil byte.

    /// <summary>The map's header node, which is also its past-the-end node: embedded on Linux, a pointer to it on
    /// Windows.</summary>
    public static readonly int TreeHead = Field("(abi)", "tree.head");
    /// <summary>How many entries the map holds; none is the identity transform.</summary>
    public static readonly int TreeCount = Field("(abi)", "tree.count");
    /// <summary>A node's parent, which on the header node is the tree's root -- null on Linux, the header itself on
    /// Windows, while the tree is empty.</summary>
    public static readonly int TreeParent = Field("(abi)", "tree.parent");
    /// <summary>A node's key, the scope its transform applies to, and the transform itself, which the pair's
    /// 8-byte alignment puts a word further on.</summary>
    public static readonly int TreeKey = Field("(abi)", "tree.key");
    /// <inheritdoc cref="TreeKey"/>
    public static readonly int TreeMapped = Field("(abi)", "tree.mapped");

    /// <summary>One lerped value of a <c>CAurObjectVisualTransformData</c>, ten of which it holds: the engine's own
    /// <c>operator new</c> sizes say 0x40 (Linux @0x7bf41b over the map node, Windows rva 0x368626).</summary>
    public static readonly int LerpFloatSizeof = Field<int>("LerpFloat", "sizeof");
    /// <summary>The front half of a LerpFloat: what the wire carries and what each build's copy constructor copies
    /// field by field (Linux @0x7bf42c, Windows rva 0x35a50a). The back half is a timing cache <c>Refresh</c> and
    /// <c>Progress</c> rewrite as a lerp runs, which is why a fingerprint hashes these bytes whole and takes no
    /// value out of them.</summary>
    public static readonly int LerpDeclared = Field<byte>("LerpFloat", "declared", count: 32);
}
