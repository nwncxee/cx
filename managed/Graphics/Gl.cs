using CX.Framework;

namespace CX.Graphics;

/// <summary>
/// The OpenGL entry points the owned draws call. The client requests a CORE profile (3.3), so
/// there is no fixed-function pipeline -- everything goes through shaders/VAO/VBO. Modern GL is
/// not a guaranteed libGL export, so every pointer resolves through the client's exported
/// SDL_GL_GetProcAddress at first draw, with a context current; all calls are allocation-free.
/// </summary>
public static unsafe class Gl
{
    // GLenum constants used by the text path.
    public const uint TEXTURE_2D = 0x0DE1, BLEND = 0x0BE2, DEPTH_TEST = 0x0B71;
    /// <summary>glGetTexLevelParameteriv queries: the level's internal format, and its alpha channel width.</summary>
    public const uint TEXTURE_INTERNAL_FORMAT = 0x1003, TEXTURE_ALPHA_SIZE = 0x805F;
    /// <summary>The level queries the owned-texture copy sizes itself from -- is this level a block
    /// format, how many bytes does it occupy, and (when it is not) how wide is each channel.</summary>
    public const uint TEXTURE_COMPRESSED = 0x86A1, TEXTURE_COMPRESSED_IMAGE_SIZE = 0x86A0,
                      TEXTURE_RED_SIZE = 0x805C, TEXTURE_GREEN_SIZE = 0x805D, TEXTURE_BLUE_SIZE = 0x805E;
    public const uint TEXTURE_WIDTH = 0x1000, TEXTURE_HEIGHT = 0x1001;
    /// <summary>The internal-format queries the block-format probe asks, and the swizzle the
    /// engine sets on a one-channel texture.</summary>
    public const uint INTERNALFORMAT_SUPPORTED = 0x826F, TEXTURE_COMPRESSED_BLOCK_WIDTH = 0x82B1,
                      TEXTURE_COMPRESSED_BLOCK_HEIGHT = 0x82B2, TEXTURE_COMPRESSED_BLOCK_SIZE = 0x82B3;
    public const uint CULL_FACE = 0x0B44, SCISSOR_TEST = 0x0C11;
    public const uint MULTISAMPLE = 0x809D, STENCIL_TEST = 0x0B90;
    /// <summary>The face <c>glCullFace</c> discards. BACK is the engine's own default
    /// (<c>Scene::RenderDrawBucket</c> @0x46d060 restores <c>SetCullFace(1)</c> on every exit).</summary>
    public const uint BACK = 0x0405;
    /// <summary>GL_DEPTH_CLAMP. The Alloy sky replays a dome the engine drew under its OWN projection
    /// (far 1000.0) beneath the world's, so it can cross the world far plane; clamping keeps the far-plane
    /// fragments rather than letting them clip into a hole at the horizon.</summary>
    public const uint DEPTH_CLAMP = 0x864F;
    public const uint SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303, ONE_MINUS_SRC_COLOR = 0x0301;
    public const uint FUNC_ADD = 0x8006, MIN = 0x8007;
    public const uint COLOR_ATTACHMENT1 = 0x8CE1;
    public const uint COLOR_ATTACHMENT2 = 0x8CE2, COLOR_ATTACHMENT3 = 0x8CE3;
    /// <summary>The deferred G-buffer's first attachment (<c>GBuffer.FirstAttachment</c>); the pass
    /// names the next as <c>COLOR_ATTACHMENT4 + i</c>, the enums being contiguous.</summary>
    public const uint COLOR_ATTACHMENT4 = 0x8CE4;
    public const uint POLYGON_OFFSET_FILL = 0x8037;
    public const uint TRIANGLE_STRIP = 0x0005;
    public const uint TRIANGLES = 0x0004, FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, FALSE = 0;
    public const uint BYTE = 0x1400, SHORT = 0x1402;
    /// <summary>The packed signed 10/10/10/2 vertex type: one 32-bit word, x in the low ten bits and the
    /// 2-bit lane in the top two. Normalized, a lane reads back as <c>c / (2^(bits-1) - 1)</c>.</summary>
    public const uint INT_2_10_10_10_REV = 0x8D9F;
    public const uint ARRAY_BUFFER = 0x8892, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, GEOMETRY_SHADER = 0x8DD9;
    public const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    /// <summary>KHR/ARB_parallel_shader_compile: has the driver finished the object's compile or link?</summary>
    public const uint COMPLETION_STATUS = 0x91B1;
    /// <summary>GL 4.6 SPIR-V: the one binary format a shader blob may be in.</summary>
    public const uint SHADER_BINARY_FORMAT_SPIR_V = 0x9551;
    public const uint TEXTURE0 = 0x84C0;
    public const uint R8 = 0x8229, RED = 0x1903;
    /// <summary>glClearBufferfv's buffer selector.</summary>
    public const uint COLOR = 0x1800;
    /// <summary>The hi-Z pyramid's format -- one float per texel, holding the MAX window depth of the
    /// region it covers.</summary>
    public const uint R32F = 0x822E, RG32F = 0x8230;
    public const uint TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800;
    public const uint TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, TEXTURE_WRAP_R = 0x8072;
    public const int  NEAREST = 0x2600, LINEAR = 0x2601, CLAMP_TO_EDGE = 0x812F, REPEAT = 0x2901;
    // Cube map (shadow cubes). Faces are POSITIVE_X + 0..5 (+X,-X,+Y,-Y,+Z,-Z).
    public const uint TEXTURE_CUBE_MAP = 0x8513, TEXTURE_CUBE_MAP_POSITIVE_X = 0x8515, TEXTURE_BINDING_CUBE_MAP = 0x8514;
    public const uint TEXTURE_2D_ARRAY = 0x8C1A, TEXTURE_BINDING_2D_ARRAY = 0x8C1D;
    /// <summary>Cube map array (GL 4.0) -- the local-light shadow cubes. Layer 6*cube + face, so
    /// <c>glFramebufferTextureLayer</c> addresses one face. <c>TEXTURE_CUBE_MAP_SEAMLESS</c> is a global
    /// enable, not a texture parameter: it makes filtering blend across face borders, which is what removes
    /// the seam the interim cubes fought with an over-scanned face frustum.</summary>
    public const uint TEXTURE_CUBE_MAP_ARRAY = 0x9009, TEXTURE_BINDING_CUBE_MAP_ARRAY = 0x900A,
                      TEXTURE_CUBE_MAP_SEAMLESS = 0x884F;
    public const uint UNPACK_ALIGNMENT = 0x0CF5, UNPACK_ROW_LENGTH = 0x0CF2, UNPACK_SKIP_ROWS = 0x0CF3, UNPACK_SKIP_PIXELS = 0x0CF4;
    /// <summary>The BCn transcode's readback alignment and its two S3TC targets (EXT_texture_compression_s3tc,
    /// universally present -- the engine's own DDS path uploads these very enums).</summary>
    public const uint PACK_ALIGNMENT = 0x0D05,
                      COMPRESSED_RGB_S3TC_DXT1 = 0x83F0, COMPRESSED_RGBA_S3TC_DXT5 = 0x83F3;
    /// <summary>The two PBO bindings that would silently turn a client pointer into a buffer offset.</summary>
    public const uint PIXEL_PACK_BUFFER = 0x88EB, PIXEL_UNPACK_BUFFER = 0x88EC,
                      PIXEL_PACK_BUFFER_BINDING = 0x88ED, PIXEL_UNPACK_BUFFER_BINDING = 0x88EF;
    // glGetIntegerv pnames for save/restore.
    public const uint VIEWPORT = 0x0BA2, CURRENT_PROGRAM = 0x8B8D, VERTEX_ARRAY_BINDING = 0x85B5;
    public const uint ACTIVE_TEXTURE = 0x84E0, TEXTURE_BINDING_2D = 0x8069;
    /// <summary>What a texture unit's 3D target and an image unit hold (the latter by glGetIntegeri_v).</summary>
    public const uint TEXTURE_BINDING_3D = 0x806A, IMAGE_BINDING_NAME = 0x8F3A;
    public const uint SCISSOR_BOX = 0x0C10;
    public const uint FRAMEBUFFER_BINDING = 0x8CA6, DEPTH_WRITEMASK = 0x0B72;
    // FBO / depth-pass (shadow map).
    public const uint FRAMEBUFFER = 0x8D40, DEPTH_ATTACHMENT = 0x8D00, FRAMEBUFFER_COMPLETE = 0x8CD5;
    // Alloy world draw: engine-VAO attribute harvest in the 4.6 context.
    public const uint ELEMENT_ARRAY_BUFFER_BINDING = 0x8895;
    public const uint VERTEX_ATTRIB_ARRAY_ENABLED = 0x8622, VERTEX_ATTRIB_ARRAY_SIZE = 0x8623,
                      VERTEX_ATTRIB_ARRAY_STRIDE = 0x8624, VERTEX_ATTRIB_ARRAY_TYPE = 0x8625,
                      VERTEX_ATTRIB_ARRAY_BUFFER_BINDING = 0x889F,
                      VERTEX_ATTRIB_ARRAY_POINTER = 0x8645;
    public const uint COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE = 0x8CD0,
                      FRAMEBUFFER_ATTACHMENT_OBJECT_NAME = 0x8CD1, RENDERBUFFER = 0x8D41,
                      FRAMEBUFFER_SRGB = 0x8DB9;
    public const uint DEPTH_COMPONENT = 0x1902, DEPTH_COMPONENT24 = 0x81A6, DEPTH_COMPONENT16 = 0x81A5, UNSIGNED_INT = 0x1405;
    public const uint DEPTH_BUFFER_BIT = 0x0100, COLOR_BUFFER_BIT = 0x4000, LESS = 0x0201, LEQUAL = 0x0203, GREATER = 0x0204, ALWAYS = 0x0207, NONE = 0;
    /// <summary>GL_EQUAL. The highlight mask tests a cutout batch against the depth its own colour
    /// pass wrote with the discard applied, so only the surviving fragments match.</summary>
    public const uint EQUAL = 0x0202;
    /// <summary>The current clear colour, as glGetFloatv reads it back -- the engine's own value, so
    /// the Alloy HDR target starts from what the engine cleared to instead of from a guess.</summary>
    public const uint COLOR_CLEAR_VALUE = 0x0C22;
    /// <summary>The depth-attachment probe the Alloy framebuffer copy sizes itself from. A blit of
    /// DEPTH_BUFFER_BIT needs the two attachments to agree on format, so the copy allocates from what the
    /// engine's own attachment reports rather than assuming 24-bit. DEPTH (0x1801) is the attachment POINT
    /// the query takes for the default framebuffer, where DEPTH_ATTACHMENT is not a legal name.</summary>
    public const uint FRAMEBUFFER_ATTACHMENT_DEPTH_SIZE = 0x8216, FRAMEBUFFER_ATTACHMENT_STENCIL_SIZE = 0x8217,
                      DEPTH = 0x1801, STENCIL = 0x1802, STENCIL_ATTACHMENT = 0x8D20;
    /// <summary>FLOAT vs UNSIGNED_NORMALIZED for an attachment's components. The one thing bit widths
    /// cannot tell apart -- DEPTH_COMPONENT32 and DEPTH_COMPONENT32F both report 32 -- and a depth blit
    /// between the two is INVALID_OPERATION, so the owned depth allocation has to ask.</summary>
    public const uint FRAMEBUFFER_ATTACHMENT_COMPONENT_TYPE = 0x8211;
    /// <summary>The engine's own glClearDepth value, read in the engine context beside
    /// COLOR_CLEAR_VALUE. Per-context state, so the owned 4.6 context cannot be asked for it.</summary>
    public const uint DEPTH_CLEAR_VALUE = 0x0B73;
    public const uint DEPTH_COMPONENT32F = 0x8CAC, DEPTH24_STENCIL8 = 0x88F0, DEPTH_STENCIL = 0x84F9,
                      UNSIGNED_INT_24_8 = 0x84FA, DEPTH_STENCIL_ATTACHMENT = 0x821A, STENCIL_BUFFER_BIT = 0x0400,
                      DEPTH_COMPONENT32 = 0x81A7, DEPTH32F_STENCIL8 = 0x8CAD,
                      UNSIGNED_SHORT = 0x1403,
                      RENDERBUFFER_INTERNAL_FORMAT = 0x8D44;
    /// <summary>The IBL probe: a half-float colour cube, mip-filtered by roughness. RGBA16F because the
    /// probe stores LINEAR radiance -- an 8-bit target would band the sky and clip anything a torch overbrights.
    /// <c>TEXTURE_MAX_LEVEL</c> caps the chain glGenerateMipmap builds, which is also the LOD the shader's
    /// roughness maps onto.</summary>
    public const uint RGBA16F = 0x881A, RGBA = 0x1908, HALF_FLOAT = 0x140B, TEXTURE_MAX_LEVEL = 0x813D;
    /// <summary>The unsized base format. Never a legal glTexStorage2D internalformat -- named so that a
    /// driver answering it for TEXTURE_INTERNAL_FORMAT can be refused rather than promoted.</summary>
    public const uint RGB = 0x1907;
    public const int LINEAR_MIPMAP_LINEAR = 0x2703;
    /// <summary>The hi-Z pyramid's minification filter -- an INTERPOLATED max is not a max.</summary>
    public const int NEAREST_MIPMAP_NEAREST = 0x2700;
    /// <summary>The HDR resolve: the split read/draw binding points glBlitFramebuffer works between, and
    /// the level clamps that keep a bloom chain's read level out of the range the draw level is attached in
    /// (sampling a level of a texture another level of which is the current attachment is only defined while
    /// BASE/MAX_LEVEL exclude it).</summary>
    public const uint READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9;
    public const uint TEXTURE_BASE_LEVEL = 0x813C;
    /// <summary>The per-texture component swizzle. Default is the identity (R,G,B,A), but the engine sets it
    /// for its single-channel legacy formats -- so a texture COPIED rather than re-uploaded has to carry
    /// these across or it samples differently from the original it is standing in for.</summary>
    public const uint TEXTURE_SWIZZLE_R = 0x8E42, TEXTURE_SWIZZLE_G = 0x8E43,
                      TEXTURE_SWIZZLE_B = 0x8E44, TEXTURE_SWIZZLE_A = 0x8E45;
    /// <summary>Additive blend factors, for the bloom chain's upsample accumulation.</summary>
    public const uint ZERO = 0, ONE = 1;
    /// <summary>Grass softening. <c>TEXTURE_MAX_ANISOTROPY</c> is core since 4.6 (same value as the old
    /// EXT), and <c>MAX_TEXTURE_MAX_ANISOTROPY</c> is the driver's ceiling to clamp against.
    /// <c>TEXTURE_LOD_BIAS</c> is sampler state, so it can be set per-draw through a sampler object without
    /// touching the engine's own texture parameters.</summary>
    public const uint TEXTURE_MAX_ANISOTROPY = 0x84FE, MAX_TEXTURE_MAX_ANISOTROPY = 0x84FF,
                      TEXTURE_LOD_BIAS = 0x8501;
    /// <summary>Sample count of the bound draw framebuffer. 1 (or 0) means alpha-to-coverage has nothing to
    /// modulate and the Alloy grass pass falls back to dithering its cutout edge.</summary>
    public const uint SAMPLES = 0x80A9, SAMPLE_BUFFERS = 0x80A8;
    /// <summary>The grass displacement field's format -- RG the signed XY push, B the press-down.</summary>
    public const uint RGBA8 = 0x8058;
    /// <summary>The lightmap's irradiance page format, and a PBO readback's usage hint.</summary>
    public const uint R11F_G11F_B10F = 0x8C3A, STREAM_READ = 0x88E1;
    /// <summary>BPTC (GL 4.2 core): BC7 for 8-bit colour, BC6H unsigned for HDR -- 16 bytes per 4x4 block.</summary>
    public const uint COMPRESSED_RGBA_BPTC_UNORM = 0x8E8C, COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT = 0x8E8F;
    /// <summary>The sized formats an unsized engine upload has to be promoted to before
    /// <see cref="TextureStorage2D"/> will take it, and the unsized enums the engine actually uses
    /// (<c>glTexImage2D</c> @0x4cad3d passes GL_RGB/GL_RGBA/GL_RED verbatim).</summary>
    public const uint RGB8 = 0x8051, RG8 = 0x822B, RG = 0x8227, RGB_ = 0x1907, RGBA_ = 0x1908, RG16F = 0x822F;
    /// <summary>The rest of the sized colour formats a native stage may ask its own images for
    /// (<c>docs/shader-overrides.md</c>, "Native stages, images and passes"); GL 4.2 image-load-store formats.</summary>
    public const uint RGBA32F = 0x8814, R16F = 0x822D;
    /// <summary>A volume texture -- a native stage's <c>tex3d</c> image, and the only target Alloy binds layered.</summary>
    public const uint TEXTURE_3D = 0x806F;
    /// <summary>Uniform enumeration (<see cref="GetActiveUniform"/>): how many a program has and the longest
    /// name, so a caller sizes one buffer instead of guessing.</summary>
    public const uint ACTIVE_UNIFORMS = 0x8B86, ACTIVE_UNIFORM_MAX_LENGTH = 0x8B87;
    /// <summary>A compute program's declared <c>local_size</c>, three ints -- what a dispatch divides its
    /// work extent by, so the group size is read off the program instead of restated in C#.</summary>
    public const uint COMPUTE_WORK_GROUP_SIZE = 0x8267;
    /// <summary>The four GLSL uniform types a native stage's image may be declared as: a sampler binds on a
    /// TEXTURE unit, an image on an IMAGE unit, and the shape has to agree. Anything else refuses the stage.</summary>
    public const uint SAMPLER_2D = 0x8B5E, SAMPLER_3D = 0x8B5F, IMAGE_2D = 0x904D, IMAGE_3D = 0x904E;
    /// <summary>The plain-value uniform types a diagnostic reads back: scalars, vectors and square matrices.</summary>
    public const uint INT = 0x1404, FLOAT_VEC2 = 0x8B50, FLOAT_VEC3 = 0x8B51, FLOAT_VEC4 = 0x8B52, INT_VEC2 = 0x8B53,
        INT_VEC3 = 0x8B54, INT_VEC4 = 0x8B55, FLOAT_MAT3 = 0x8B5B, FLOAT_MAT4 = 0x8B5C;
    /// <summary>Alpha-to-coverage -- the fragment's alpha becomes a COVERAGE MASK over the target's
    /// samples. Each sample is still written whole or not at all, so it is antialiasing without ever
    /// producing a partially transparent depth-writing fragment. Inert where SAMPLES is 1.</summary>
    public const uint SAMPLE_ALPHA_TO_COVERAGE = 0x809E;
    /// <summary>The implementation's sample ceiling for a multisampled target.</summary>
    public const uint MAX_SAMPLES = 0x8D57;
    public const uint TEXTURE_COMPARE_MODE = 0x884C, TEXTURE_COMPARE_FUNC = 0x884D, COMPARE_REF_TO_TEXTURE = 0x884E;
    // The light rig and the deferred pass: compute dispatch, SSBOs and the GPU timer for their budgets.
    // All 4.6-core, so they exist only inside the share-listed context.
    public const uint COMPUTE_SHADER = 0x91B9, SHADER_STORAGE_BUFFER = 0x90D2;
    /// <summary>The pass block's target (<c>cx_pass.glsl</c>'s <c>CxPass</c>).</summary>
    public const uint UNIFORM_BUFFER = 0x8A11;
    public const uint SHADER_STORAGE_BARRIER_BIT = 0x2000;
    /// <summary>The two barrier bits the cull dispatch needs after it -- its writes are consumed as
    /// indirect commands and as a parameter-buffer count, neither of which SHADER_STORAGE alone covers.</summary>
    public const uint COMMAND_BARRIER_BIT = 0x0040, BUFFER_UPDATE_BARRIER_BIT = 0x0200;
    /// <summary>A compute write consumed as a divisor-1 vertex attribute (the grass scatter's stream).</summary>
    public const uint VERTEX_ATTRIB_ARRAY_BARRIER_BIT = 0x0001;
    /// <summary>An image store read back by the next level's dispatch (image) and by the lit stage (fetch).</summary>
    public const uint SHADER_IMAGE_ACCESS_BARRIER_BIT = 0x0020, TEXTURE_FETCH_BARRIER_BIT = 0x0008, TEXTURE_UPDATE_BARRIER_BIT = 0x0100, PIXEL_BUFFER_BARRIER_BIT = 0x0080, READ_ONLY = 0x88B8, WRITE_ONLY = 0x88B9, READ_WRITE = 0x88BA;
    public const uint ALL_BARRIER_BITS = 0xFFFFFFFF;   // not-an-offset: GL_ALL_BARRIER_BITS
    /// <summary>A compute write to a buffer the lit stage then reads as a uniform block (the probe's SH9).</summary>
    public const uint UNIFORM_BARRIER_BIT = 0x0004;
    /// <summary>An image store into a texture a LATER PASS renders to as an attachment -- the deferred
    /// pass writes the HDR target, which grass and the blended class go on blending into.</summary>
    public const uint FRAMEBUFFER_BARRIER_BIT = 0x0400;
    public const uint NUM_EXTENSIONS = 0x821D, EXTENSIONS = 0x1F03;
    /// <summary>The implementation limits Alloy's stages exceed the GL 4.6 floor on (queried once, logged by name).</summary>
    public const uint MAX_VERTEX_SHADER_STORAGE_BLOCKS = 0x90D6, MAX_SHADER_STORAGE_BUFFER_BINDINGS = 0x90DD,
                      MAX_TEXTURE_IMAGE_UNITS = 0x8872, MAX_COMBINED_TEXTURE_IMAGE_UNITS = 0x8B4D;
    public const uint TEXTURE_2D_MULTISAMPLE = 0x9100;
    public const uint TIME_ELAPSED = 0x88BF, QUERY_RESULT = 0x8866, QUERY_RESULT_AVAILABLE = 0x8867, TIMESTAMP = 0x8E28;
    /// <summary>glBeginQuery target counting fragments that passed depth+stencil: the overdraw meter
    /// for the Alloy colour pass. Not ANY_SAMPLES_PASSED -- that one is boolean and answers a different
    /// question.</summary>
    public const uint SAMPLES_PASSED = 0x8914;
    public const uint PRIMITIVES_GENERATED = 0x8C87;
    public const uint FRAGMENT_SHADER_INVOCATIONS = 0x82F4;
    // Multidraw-indirect from persistent-mapped storage. The MAP_* bits are glNamedBufferStorage flags AND
    // glMapNamedBufferRange flags: the map's set must be a subset of the storage's or the map fails.
    public const uint DRAW_INDIRECT_BUFFER = 0x8F3F;
    /// <summary>GL_PARAMETER_BUFFER (GL 4.6): where <see cref="MultiDrawElementsIndirectCount"/> reads its
    /// draw count from, so the count never has to reach the CPU.</summary>
    public const uint PARAMETER_BUFFER = 0x80EE;
    public const uint MAP_WRITE_BIT = 0x0002, MAP_PERSISTENT_BIT = 0x0040, MAP_COHERENT_BIT = 0x0080;
    /// <summary>The read side of the same flag set -- for mapping an existing buffer back to the host,
    /// which is a readback and belongs to diagnostics, not to a frame.</summary>
    public const uint MAP_READ_BIT = 0x0001;
    public const uint SYNC_GPU_COMMANDS_COMPLETE = 0x9117, SYNC_FLUSH_COMMANDS_BIT = 0x0001;
    public const uint ALREADY_SIGNALED = 0x911A, CONDITION_SATISFIED = 0x911C;

    public static delegate* unmanaged<uint, int*, void> GetIntegerv;
    public static delegate* unmanaged<uint, uint, int*, void> GetIntegeri_v;
    public static delegate* unmanaged<uint, byte> IsEnabled;   // GLboolean glIsEnabled(GLenum)
    public static delegate* unmanaged<uint> GetError;          // GLenum glGetError(void)
    public static delegate* unmanaged<void> Finish;            // void glFinish(void)
    /// <summary>Empty the error queue without trusting it to end. A context the driver has given up on
    /// answers an error forever, and an unbounded drain there is a hang rather than a diagnostic. Returns
    /// how many codes it drained; safe before <see cref="Init"/> (no entry point, nothing drained).</summary>
    public static int DrainErrors(int max = 16)
    {
        int n = 0;
        if (GetError != null) while (n < max && GetError() != 0) n++;
        return n;
    }
    public static delegate* unmanaged<uint, void> Enable, Disable, ActiveTexture, UseProgram, BindVertexArray,
                                                  CullFace;
    public static delegate* unmanaged<uint, uint, void> BindTexture, BindBuffer, BlendFunc;
    /// <summary>glBlendFunci (GL 4.0): a blend pair per draw buffer -- the OIT accumulation pair needs two.</summary>
    public static delegate* unmanaged<uint, uint, uint, void> BlendFunci;
    /// <summary>glBlendFuncSeparatei (GL 4.0): separate colour and alpha pairs for one draw buffer -- the decal
    /// layer over-blends its colour and multiplies its alpha down.</summary>
    public static delegate* unmanaged<uint, uint, uint, uint, uint, void> BlendFuncSeparatei;
    /// <summary>glBlendEquationi (GL 4.0): one draw buffer's blend equation -- the decal layer keeps its nearest
    /// decal's depth under MIN.</summary>
    public static delegate* unmanaged<uint, uint, void> BlendEquationi;
    /// <summary>glDrawBuffers: which colour attachments the bound FBO writes.</summary>
    public static delegate* unmanaged<int, uint*, void> DrawBuffers;
    public static delegate* unmanaged<int, uint*, void> GenTextures, GenVertexArrays, GenBuffers, DeleteBuffers;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    /// <summary>glTexSubImage2D (GL 1.1): target, level, xoffset, yoffset, width, height, format, type, data.
    /// Writes into an already-allocated level.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexSubImage2D;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
    /// <summary>Per-LEVEL texture state -- the internal format and channel sizes. The one way to ask a
    /// texture whether it actually carries alpha, which no engine-side opacity flag answers reliably.</summary>
    public static delegate* unmanaged<uint, int, uint, int*, void> GetTexLevelParameteriv;
    public static delegate* unmanaged<uint, int, void> PixelStorei;
    public static delegate* unmanaged<uint, IntPtr, void*, uint, void> BufferData;
    public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, int, int, void> DrawArrays;
    public static delegate* unmanaged<int, int, int, int, void> Scissor;   // glScissor(x,y,w,h)
    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader, DeleteShader;
    /// <summary>glShaderBinary(count, shaders*, binaryFormat, binary*, length) -- loads a SPIR-V module into
    /// shader objects. Nothing is compiled until <see cref="SpecializeShader"/> names an entry point.</summary>
    public static delegate* unmanaged<int, uint*, uint, void*, int, void> ShaderBinary;
    /// <summary>glSpecializeShader(shader, entryPoint*, numConstants, index*, value*) -- the SPIR-V
    /// counterpart of glCompileShader; COMPILE_STATUS reports its result.</summary>
    public static delegate* unmanaged<uint, byte*, uint, uint*, uint*, void> SpecializeShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv, GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    /// <summary>glTextureView(texture, target, origtexture, internalformat, minlevel, numlevels, minlayer, numlayers).</summary>
    public static delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, void> TextureView;
    public static delegate* unmanaged<int, uint*, void> GenSamplers;
    /// <summary>glClearBufferfv: clear one attachment of the bound FBO without touching the clear-colour state.</summary>
    public static delegate* unmanaged<uint, int, float*, void> ClearBufferfv;
    /// <summary>glClearBufferuiv: the integer-attachment clear (a float clear on an R32UI target is undefined).</summary>
    public static delegate* unmanaged<uint, int, uint*, void> ClearBufferuiv;
    /// <summary>glClearTexImage (GL 4.4): tex, level, format, type, data -- a NULL data fills zeros. Clears a
    /// texture whatever framebuffer it is or is not attached to, so no draw-buffer state has to be arranged first.</summary>
    public static delegate* unmanaged<uint, int, uint, uint, void*, void> ClearTexImage;
    public static delegate* unmanaged<uint, uint, void> BindSampler;
    public static delegate* unmanaged<uint, uint, int, void> SamplerParameteri;
    public static delegate* unmanaged<uint, uint, float, void> SamplerParameterf;
    /// <summary>A LINEAR, clamp-to-edge sampler object -- the screen-sized-copy sampler five stages minted
    /// by hand. 0 when the context has no sampler objects or the driver refused the name.</summary>
    public static uint LinearClampSampler()
    {
        if (GenSamplers == null || SamplerParameteri == null) return 0;
        uint s; GenSamplers(1, &s);
        if (s == 0) return 0;
        SamplerParameteri(s, TEXTURE_MIN_FILTER, LINEAR);
        SamplerParameteri(s, TEXTURE_MAG_FILTER, LINEAR);
        SamplerParameteri(s, TEXTURE_WRAP_S, CLAMP_TO_EDGE);
        SamplerParameteri(s, TEXTURE_WRAP_T, CLAMP_TO_EDGE);
        return s;
    }
    public static delegate* unmanaged<uint, int, float*, void> GetUniformfv;
    public static delegate* unmanaged<uint, int, int*, void> GetUniformiv;
    public static delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, int, void*, void> GetTextureSubImage;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, void> LinkProgram;
    /// <summary>glGetProgramBinary(program, bufSize, length*, format*, binary*) / glProgramBinary(program, format,
    /// binary*, length) / glProgramParameteri: the <see cref="ProgramBinary"/> disk cache (GL 4.1 core).</summary>
    public static delegate* unmanaged<uint, int, int*, uint*, void*, void> GetProgramBinary;
    public static delegate* unmanaged<uint, uint, void*, int, void> ProgramBinaryLoad;
    public static delegate* unmanaged<uint, uint, int, void> ProgramParameteri;
    public const uint PROGRAM_BINARY_RETRIEVABLE_HINT = 0x8257, PROGRAM_BINARY_LENGTH = 0x8741;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, void> Uniform2f;
    public static delegate* unmanaged<int, int, int, void> Uniform2i;
    public static delegate* unmanaged<int, uint, uint, void> Uniform2ui;
    public static delegate* unmanaged<int, float, float, float, void> Uniform3f;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, int, float*, void> Uniform1fv;   // (loc, count, ptr) -- per-slot floats
    public static delegate* unmanaged<int, int, int*, void> Uniform1iv;     // (loc, count, ptr) -- per-slot ints
    public static delegate* unmanaged<int, int, float*, void> Uniform3fv;   // (loc, count, ptr) -- per-slot vec3
    public static delegate* unmanaged<int, int, float*, void> Uniform4fv;   // (loc, count, ptr) -- per-slot vec4
    // Alloy program cloning (ADR 0007 phase 3): enumerate + mirror engine-program uniforms.
    public static delegate* unmanaged<int, int, float*, void> Uniform2fv;
    public static delegate* unmanaged<int, int, int*, void> Uniform2iv, Uniform4iv;
    /// <summary>glGetActiveUniform: prog, index, bufSize, length*, size*, type*, name* -- uniform enumeration.</summary>
    public static delegate* unmanaged<uint, uint, int, int*, int*, uint*, byte*, void> GetActiveUniform;
    public const uint R32UI = 0x8236, RED_INTEGER = 0x8D94;
    /// <summary>The G-buffer's three integer words and the upload format its clear names, plus the
    /// shadow visibility record's key pair (<c>cx_shrec</c>: the four packed slots and the surface key).</summary>
    public const uint RGBA32UI = 0x8D70, RGBA_INTEGER = 0x8D99, RG32UI = 0x823C;
    /// <summary>Zero the first <paramref name="bytes"/> of <paramref name="buf"/> on the GPU. A client-side
    /// upload into a buffer the previous dispatch may still read is an implicit sync the spec permits
    /// the driver to take as a stall (Khronos, Buffer Object Streaming); a clear has no such term. Unmeasured.</summary>
    public static void ZeroBuffer(uint buf, long bytes)
        => ClearNamedBufferSubData(buf, R32UI, IntPtr.Zero, (IntPtr)bytes, RED_INTEGER, UNSIGNED_INT, null);
    /// <summary>Mint <paramref name="buf"/> on first use and (re)allocate it as DYNAMIC_DRAW storage when
    /// <paramref name="grow"/> asks; a fresh name always allocates. New storage is zeroed: some drivers hand
    /// back stale memory, and a count or indirect command read from it can hang the GPU. Touches no binding.</summary>
    public static void EnsureStorage(ref uint buf, long bytes, bool grow)
    {
        if (buf == 0) { uint b; CreateBuffers(1, &b); buf = b; grow = true; }
        if (!grow) return;
        long n = Math.Max(bytes, 4);
        NamedBufferData(buf, (IntPtr)n, null, DYNAMIC_DRAW);
        ZeroBuffer(buf, n & ~3L);
    }
    public static delegate* unmanaged<uint, uint, uint, void> BindBufferBase;
    public static delegate* unmanaged<uint, uint, uint, IntPtr, IntPtr, void> BindBufferRange;
    public static delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    // FBO / depth-pass entry points.
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers, DeleteFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindFramebuffer;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    /// <summary>glFramebufferTextureLayer (GL 3.2): target, attachment, texture, level, layer -- attaches one
    /// layer of an array texture (the depth-cube 2D-array render path).</summary>
    public static delegate* unmanaged<uint, uint, uint, int, int, void> FramebufferTextureLayer;
    /// <summary>glFramebufferTexture (GL 3.2): target, attachment, texture, level -- attaches EVERY layer of
    /// an array texture at once (a layered attachment), which is what lets a stage route draws by
    /// <c>gl_Layer</c> (the layered bake).</summary>
    public static delegate* unmanaged<uint, uint, uint, int, void> FramebufferTexture;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    /// <summary>glBlitFramebuffer (GL 3.0): srcX0,srcY0,srcX1,srcY1, dstX0,dstY0,dstX1,dstY1, mask, filter.
    /// Colour blits convert format (RGBA8 -&gt; RGBA16F is legal); depth blits do NOT, which is why the
    /// HDR target shares the engine's depth attachment rather than copying it.</summary>
    public static delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebuffer;
    public static delegate* unmanaged<float, float, float, float, void> ClearColor;
    public static delegate* unmanaged<uint, void> DrawBuffer, ReadBuffer, DepthFunc, Clear;
    /// <summary>glReadPixels: x, y, w, h, format, type, out. Stalls the pipeline -- diagnostic paths only
    /// (the /water diag reads the framebuffer copy back once to settle what is actually in it).</summary>
    public static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;
    public static delegate* unmanaged<byte, void> DepthMask;
    public static delegate* unmanaged<float, float, void> PolygonOffset;
    public static delegate* unmanaged<byte, byte, byte, byte, void> ColorMask;
    /// <summary>glFrontFace: which winding is front. A mirrored view flips every triangle's winding.</summary>
    public static delegate* unmanaged<uint, void> FrontFace;
    public const uint CW = 0x0900, CCW = 0x0901;
    /// <summary>glColorMaski: the write mask of ONE draw buffer (GL 3.0).</summary>
    public static delegate* unmanaged<uint, byte, byte, byte, byte, void> ColorMaski;
    public static delegate* unmanaged<double, void> ClearDepth;
    public static delegate* unmanaged<double, double, void> DepthRange;
    public static delegate* unmanaged<uint, float*, void> GetFloatv;
    public static delegate* unmanaged<int, int, int, int, void> Viewport;
    public static delegate* unmanaged<int, uint*, void> DeleteTextures;
    /// <summary>glCopyImageSubData (GL 4.3 / ARB_copy_image): srcName, srcTarget, srcLevel, sx, sy, sz,
    /// dstName, dstTarget, dstLevel, dx, dy, dz, w, h, depth. Null on drivers without it -- callers fall back.</summary>
    public static delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int, int, int, int, void> CopyImageSubData;
    /// <summary>glMultiDrawElementsBaseVertex (GL 3.2 core): mode, counts*, type, indices**, drawcount,
    /// basevertex*. One call submits a whole batch group (ADR 0006 phase 1). Null on ancient drivers.</summary>
    public static delegate* unmanaged<uint, int*, uint, void**, int, int*, void> MultiDrawElementsBaseVertex;
    /// <summary>glDrawElementsBaseVertex (GL 3.2): mode, count, type, indices, basevertex -- one run of one
    /// mesh, the lightmap bake's per-rect draw.</summary>
    public static delegate* unmanaged<uint, int, uint, void*, int, void> DrawElementsBaseVertex;

    // Alloy world draw
    public static delegate* unmanaged<uint, uint, int*, void> GetVertexAttribiv;
    public static delegate* unmanaged<uint, uint, void**, void> GetVertexAttribPointerv;
    public static delegate* unmanaged<uint, uint, uint, int*, void> GetFramebufferAttachmentParameteriv;
    /// <summary>glBindTextureUnit (GL 4.5 DSA): unit, texture. One call where ActiveTexture+BindTexture+
    /// ActiveTexture was three -- the Alloy colour pass changes texture state ~4000 times a frame.</summary>
    public static delegate* unmanaged<uint, uint, void> BindTextureUnit;
    /// <summary>ARB_bindless_texture: texture -> 64-bit handle, and residency. Presence of the extension is
    /// checked by string (GLX hands back non-null pointers for unsupported extensions).</summary>
    public static delegate* unmanaged<uint, ulong> GetTextureHandleARB;
    public static delegate* unmanaged<ulong, void> MakeTextureHandleResidentARB;
    /// <summary>The other half of residency. A texture must not be deleted while a handle to it is
    /// resident -- the handle survives the object, and a draw that reads a dead one draws garbage.</summary>
    public static delegate* unmanaged<ulong, void> MakeTextureHandleNonResidentARB;
    /// <summary>glGetStringi (GL 3.0): pname, index -- the core-profile extension enumeration.</summary>
    public static delegate* unmanaged<uint, uint, byte*> GetStringi;
    public static delegate* unmanaged<int, uint*, void> DeleteVertexArrays;
    public static delegate* unmanaged<uint, void> DeleteProgram;
    // compute: the cluster build, the deferred lighting dispatch and the bakes
    public static delegate* unmanaged<uint, uint, uint, void> DispatchCompute;
    public static delegate* unmanaged<uint, void> MemoryBarrier;
    /// <summary>glBindImageTexture (GL 4.2): unit, texture, level, layered, layer, access, format.</summary>
    public static delegate* unmanaged<uint, uint, int, byte, int, uint, uint, void> BindImageTexture;
    public static delegate* unmanaged<int, uint*, void> GenQueries;
    public static delegate* unmanaged<uint, uint, void> BeginQuery;
    public static delegate* unmanaged<uint, void> EndQuery;
    public static delegate* unmanaged<uint, uint, void> QueryCounter;
    public static delegate* unmanaged<uint, uint, int*, void> GetQueryObjectiv;
    public static delegate* unmanaged<uint, uint, ulong*, void> GetQueryObjectui64v;
    public static delegate* unmanaged<int, int, int, int, void> Uniform3i;
    // Alloy caster submission. All 4.6-core, so they exist only inside the share-listed context;
    // every one is null-checked before the path arms.
    /// <summary>glMultiDrawElementsIndirect (GL 4.3): mode, type, indirect (byte offset into the bound
    /// DRAW_INDIRECT_BUFFER), drawcount, stride (0 = tightly packed).</summary>
    public static delegate* unmanaged<uint, uint, void*, int, int, void> MultiDrawElementsIndirect;
    /// <summary>glMultiDrawElementsIndirectCount (GL 4.6 / ARB_indirect_parameters): mode, type, indirect
    /// (byte offset into the bound DRAW_INDIRECT_BUFFER), drawcount (byte offset into the bound
    /// PARAMETER_BUFFER -- a GPU-side count, so a compute cull's result never returns to the CPU),
    /// maxdrawcount, stride. GLX returns non-null for any name, so a caller gates on the extension STRING.</summary>
    public static delegate* unmanaged<uint, uint, void*, IntPtr, int, int, void> MultiDrawElementsIndirectCount;
    /// <summary>glDrawArraysInstancedBaseInstance (GL 4.2): mode, first, count, instancecount, baseinstance.
    /// A grass patch is a contiguous run inside ONE instance buffer, so it draws by naming its offset
    /// instead of rebinding a pointer. <c>gl_InstanceID</c> does NOT include the base; divisor-1 vertex
    /// attributes do, which is why the per-blade data is an attribute stream, not an SSBO indexed by id.</summary>
    public static delegate* unmanaged<uint, int, int, int, uint, void> DrawArraysInstancedBaseInstance;
    /// <summary>glDrawElementsInstancedBaseVertexBaseInstance (GL 4.2): mode, count, type, indices, instancecount,
    /// basevertex, baseinstance. The GUI draws name their record through <c>gl_BaseInstance</c>.</summary>
    public static delegate* unmanaged<uint, int, uint, void*, int, int, uint, void> DrawElementsInstancedBaseVertexBaseInstance;
    /// <summary>glDrawArraysIndirect (GL 4.0): mode, indirect (byte offset into the bound DRAW_INDIRECT_BUFFER).
    /// the grass scatter writes the instance count, so it never reaches the CPU.</summary>
    public static delegate* unmanaged<uint, void*, void> DrawArraysIndirect;
    public static delegate* unmanaged<uint, uint, IntPtr> FenceSync;
    public static delegate* unmanaged<IntPtr, uint, ulong, uint> ClientWaitSync;
    public static delegate* unmanaged<IntPtr, void> DeleteSync;

    // Direct state access: edits touch no binding, so the engine's bind cache stays true. No fallback;
    // GlContextService.Available is the one gate (it checks both contexts).
    public static delegate* unmanaged<int, uint*, void> CreateBuffers, CreateVertexArrays;
    /// <summary>glCreateTextures: target, n, names. The target is fixed at creation, unlike glGenTextures.</summary>
    public static delegate* unmanaged<uint, int, uint*, void> CreateTextures;
    public static delegate* unmanaged<uint, IntPtr, void*, uint, void> NamedBufferData, NamedBufferStorage;
    public static delegate* unmanaged<uint, IntPtr, IntPtr, void*, void> NamedBufferSubData;
    public static delegate* unmanaged<uint, uint, IntPtr, IntPtr, IntPtr, void> CopyNamedBufferSubData;
    public static delegate* unmanaged<uint, uint, IntPtr, IntPtr, uint, uint, void*, void> ClearNamedBufferSubData;
    public static delegate* unmanaged<uint, IntPtr, IntPtr, uint, void*> MapNamedBufferRange;
    public static delegate* unmanaged<uint, byte> UnmapNamedBuffer;
    /// <summary>glTextureStorage2D / 3D: texture, levels, internalFormat, width, height[, depth]. Immutable --
    /// a resize is delete + create, never a respecify.</summary>
    public static delegate* unmanaged<uint, int, uint, int, int, void> TextureStorage2D;
    public static delegate* unmanaged<uint, int, uint, int, int, int, void> TextureStorage3D;
    /// <summary>glTextureStorage2DMultisample: texture, samples, internalFormat, width, height, fixedSampleLocations.</summary>
    public static delegate* unmanaged<uint, int, uint, int, int, byte, void> TextureStorage2DMultisample;
    /// <summary>glTextureSubImage2D: texture, level, x, y, w, h, format, type, data.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TextureSubImage2D;
    /// <summary>glTextureSubImage3D: texture, level, x, y, z, w, h, depth, format, type, data.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, void*, void> TextureSubImage3D;
    /// <summary>glCompressedTextureSubImage2D: texture, level, x, y, w, h, format, imageSize, data.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, int, void*, void> CompressedTextureSubImage2D;
    /// <summary>glCompressedTextureSubImage3D: texture, level, x, y, z, w, h, depth, format, imageSize, data.</summary>
    public static delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, int, void*, void> CompressedTextureSubImage3D;
    public static delegate* unmanaged<uint, uint, int, void> TextureParameteri;
    public static delegate* unmanaged<uint, uint, int*, void> GetTextureParameteriv;
    public static delegate* unmanaged<uint, int, uint, int*, void> GetTextureLevelParameteriv;
    /// <summary>glGetInternalformativ: target, internalformat, pname, count, params.</summary>
    public static delegate* unmanaged<uint, uint, uint, int, int*, void> GetInternalformativ;
    /// <summary>glGetTextureImage: texture, level, format, type, bufSize, pixels.</summary>
    public static delegate* unmanaged<uint, int, uint, uint, int, void*, void> GetTextureImage;
    public static delegate* unmanaged<uint, void> GenerateTextureMipmap;
    /// <summary>glVertexArrayVertexBuffer: vaobj, bindingindex, buffer, offset, stride.</summary>
    public static delegate* unmanaged<uint, uint, uint, IntPtr, int, void> VertexArrayVertexBuffer;
    /// <summary>glVertexArrayAttribFormat: vaobj, attribindex, size, type, normalized, relativeoffset.</summary>
    public static delegate* unmanaged<uint, uint, int, uint, byte, uint, void> VertexArrayAttribFormat;
    /// <summary>glVertexArrayAttribBinding: vaobj, attribindex, bindingindex. glVertexArrayBindingDivisor:
    /// vaobj, bindingindex, divisor.</summary>
    public static delegate* unmanaged<uint, uint, uint, void> VertexArrayAttribBinding, VertexArrayBindingDivisor;
    public static delegate* unmanaged<uint, uint, void> EnableVertexArrayAttrib, VertexArrayElementBuffer;
    public static delegate* unmanaged<uint, uint, int*, void> GetVertexArrayiv;
    public static delegate* unmanaged<uint, uint, int*, void> GetNamedRenderbufferParameteriv;

    /// <summary>Does the CURRENT context advertise this extension? Asks whichever context is current, and two
    /// contexts on one driver need not advertise the same set.</summary>
    public static bool HasExt(ReadOnlySpan<byte> want)
    {
        if (GetStringi == null) return false;
        int n = 0; GetIntegerv(NUM_EXTENSIONS, &n);
        for (uint i = 0; i < (uint)n; i++)
        {
            byte* e = GetStringi(EXTENSIONS, i);
            if (e == null) continue;
            int k = 0;
            for (; k < want.Length && e[k] != 0; k++) if (e[k] != want[k]) break;
            if (k == want.Length && e[k] == 0) return true;
        }
        return false;
    }

    private static delegate* unmanaged<byte*, IntPtr> _getProc;
    private static bool _ready, _failed;

    /// <summary>Resolve every entry point once (context must be current). False = a required proc was
    /// missing; latched so the hot hook doesn't re-resolve or log every frame after a failure.</summary>
    public static bool Init(ExtenderLog log)
    {
        if (_ready) return true;
        if (_failed) return false;
        _getProc = (delegate* unmanaged<byte*, IntPtr>)Native.Sym(SymbolMap.SdlGlGetProcAddress);
        if (_getProc == null) { log.Info("Gl: SDL_GL_GetProcAddress unresolved"); _failed = true; return false; }

        GetIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
        GetIntegeri_v = (delegate* unmanaged<uint, uint, int*, void>)P("glGetIntegeri_v");
        GetTexLevelParameteriv = (delegate* unmanaged<uint, int, uint, int*, void>)P("glGetTexLevelParameteriv");
        IsEnabled = (delegate* unmanaged<uint, byte>)P("glIsEnabled");
        GetError = (delegate* unmanaged<uint>)P("glGetError");
        Finish = (delegate* unmanaged<void>)P("glFinish");
        Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        CullFace = (delegate* unmanaged<uint, void>)P("glCullFace");
        BlendFunc = (delegate* unmanaged<uint, uint, void>)P("glBlendFunc");
        BlendFunci = (delegate* unmanaged<uint, uint, uint, void>)P("glBlendFunci");
        BlendFuncSeparatei = (delegate* unmanaged<uint, uint, uint, uint, uint, void>)P("glBlendFuncSeparatei");
        BlendEquationi = (delegate* unmanaged<uint, uint, void>)P("glBlendEquationi");
        DrawBuffers = (delegate* unmanaged<int, uint*, void>)P("glDrawBuffers");
        ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        DeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        TexSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexSubImage2D");
        TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
        BufferData = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glBufferData");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        DrawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
        Scissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor");
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        ShaderBinary = (delegate* unmanaged<int, uint*, uint, void*, int, void>)P("glShaderBinary");
        SpecializeShader = (delegate* unmanaged<uint, byte*, uint, uint*, uint*, void>)P("glSpecializeShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        TextureView = (delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, void>)P("glTextureView");
        GenSamplers = (delegate* unmanaged<int, uint*, void>)P("glGenSamplers");
        ClearBufferfv = (delegate* unmanaged<uint, int, float*, void>)P("glClearBufferfv");
        ClearBufferuiv = (delegate* unmanaged<uint, int, uint*, void>)P("glClearBufferuiv");
        ClearTexImage = (delegate* unmanaged<uint, int, uint, uint, void*, void>)P("glClearTexImage");
        BindSampler = (delegate* unmanaged<uint, uint, void>)P("glBindSampler");
        SamplerParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glSamplerParameteri");
        SamplerParameterf = (delegate* unmanaged<uint, uint, float, void>)P("glSamplerParameterf");
        GetUniformfv = (delegate* unmanaged<uint, int, float*, void>)P("glGetUniformfv");
        GetUniformiv = (delegate* unmanaged<uint, int, int*, void>)P("glGetUniformiv");
        GetTextureSubImage = (delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, int, void*, void>)P("glGetTextureSubImage");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetProgramBinary = (delegate* unmanaged<uint, int, int*, uint*, void*, void>)P("glGetProgramBinary");
        ProgramBinaryLoad = (delegate* unmanaged<uint, uint, void*, int, void>)P("glProgramBinary");
        ProgramParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glProgramParameteri");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        Uniform2fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform2fv");
        Uniform2iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform2iv");
        Uniform4iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform4iv");
        GetActiveUniform = (delegate* unmanaged<uint, uint, int, int*, int*, uint*, byte*, void>)P("glGetActiveUniform");
        BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        BindBufferBase = (delegate* unmanaged<uint, uint, uint, void>)P("glBindBufferBase");
        BindBufferRange = (delegate* unmanaged<uint, uint, uint, IntPtr, IntPtr, void>)P("glBindBufferRange");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        Uniform2i = (delegate* unmanaged<int, int, int, void>)P("glUniform2i");
        Uniform2ui = (delegate* unmanaged<int, uint, uint, void>)P("glUniform2ui");
        Uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        Uniform1fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform1fv");
        Uniform1iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform1iv");
        Uniform3fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform3fv");
        Uniform4fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform4fv");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        FramebufferTextureLayer = (delegate* unmanaged<uint, uint, uint, int, int, void>)P("glFramebufferTextureLayer");
        FramebufferTexture = (delegate* unmanaged<uint, uint, uint, int, void>)P("glFramebufferTexture");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        BlitFramebuffer =
            (delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>)P("glBlitFramebuffer");
        ClearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        DrawBuffer = (delegate* unmanaged<uint, void>)P("glDrawBuffer");
        ReadBuffer = (delegate* unmanaged<uint, void>)P("glReadBuffer");
        ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
        DepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        Clear = (delegate* unmanaged<uint, void>)P("glClear");
        DepthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
        PolygonOffset = (delegate* unmanaged<float, float, void>)P("glPolygonOffset");
        ColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
        FrontFace = (delegate* unmanaged<uint, void>)P("glFrontFace");
        ColorMaski = (delegate* unmanaged<uint, byte, byte, byte, byte, void>)P("glColorMaski");
        ClearDepth = (delegate* unmanaged<double, void>)P("glClearDepth");
        DepthRange = (delegate* unmanaged<double, double, void>)P("glDepthRange");
        GetFloatv = (delegate* unmanaged<uint, float*, void>)P("glGetFloatv");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        CopyImageSubData = (delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int, int, int, int, void>)P("glCopyImageSubData");
        MultiDrawElementsBaseVertex = (delegate* unmanaged<uint, int*, uint, void**, int, int*, void>)P("glMultiDrawElementsBaseVertex");
        DrawElementsBaseVertex = (delegate* unmanaged<uint, int, uint, void*, int, void>)P("glDrawElementsBaseVertex");
        GetVertexAttribiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetVertexAttribiv");
        GetVertexAttribPointerv = (delegate* unmanaged<uint, uint, void**, void>)P("glGetVertexAttribPointerv");
        GetFramebufferAttachmentParameteriv = (delegate* unmanaged<uint, uint, uint, int*, void>)P("glGetFramebufferAttachmentParameteriv");
        BindTextureUnit = (delegate* unmanaged<uint, uint, void>)P("glBindTextureUnit");
        GetTextureHandleARB = (delegate* unmanaged<uint, ulong>)P("glGetTextureHandleARB");
        MakeTextureHandleResidentARB = (delegate* unmanaged<ulong, void>)P("glMakeTextureHandleResidentARB");
        MakeTextureHandleNonResidentARB = (delegate* unmanaged<ulong, void>)P("glMakeTextureHandleNonResidentARB");
        GetStringi = (delegate* unmanaged<uint, uint, byte*>)P("glGetStringi");
        DeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glDeleteVertexArrays");
        DeleteProgram = (delegate* unmanaged<uint, void>)P("glDeleteProgram");
        DispatchCompute = (delegate* unmanaged<uint, uint, uint, void>)P("glDispatchCompute");
        MemoryBarrier = (delegate* unmanaged<uint, void>)P("glMemoryBarrier");
        BindImageTexture = (delegate* unmanaged<uint, uint, int, byte, int, uint, uint, void>)P("glBindImageTexture");
        GenQueries = (delegate* unmanaged<int, uint*, void>)P("glGenQueries");
        BeginQuery = (delegate* unmanaged<uint, uint, void>)P("glBeginQuery");
        EndQuery = (delegate* unmanaged<uint, void>)P("glEndQuery");
        QueryCounter = (delegate* unmanaged<uint, uint, void>)P("glQueryCounter");
        GetQueryObjectiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetQueryObjectiv");
        GetQueryObjectui64v = (delegate* unmanaged<uint, uint, ulong*, void>)P("glGetQueryObjectui64v");
        Uniform3i = (delegate* unmanaged<int, int, int, int, void>)P("glUniform3i");
        MultiDrawElementsIndirect = (delegate* unmanaged<uint, uint, void*, int, int, void>)P("glMultiDrawElementsIndirect");
        MultiDrawElementsIndirectCount = (delegate* unmanaged<uint, uint, void*, IntPtr, int, int, void>)P("glMultiDrawElementsIndirectCount");
        // ...and the pre-4.6 spelling, which is the same function under the extension that promoted into core.
        if (MultiDrawElementsIndirectCount == null)
            MultiDrawElementsIndirectCount = (delegate* unmanaged<uint, uint, void*, IntPtr, int, int, void>)P("glMultiDrawElementsIndirectCountARB");
        DrawArraysIndirect = (delegate* unmanaged<uint, void*, void>)P("glDrawArraysIndirect");
        DrawArraysInstancedBaseInstance =
            (delegate* unmanaged<uint, int, int, int, uint, void>)P("glDrawArraysInstancedBaseInstance");
        DrawElementsInstancedBaseVertexBaseInstance =
            (delegate* unmanaged<uint, int, uint, void*, int, int, uint, void>)P("glDrawElementsInstancedBaseVertexBaseInstance");
        FenceSync = (delegate* unmanaged<uint, uint, IntPtr>)P("glFenceSync");
        ClientWaitSync = (delegate* unmanaged<IntPtr, uint, ulong, uint>)P("glClientWaitSync");
        DeleteSync = (delegate* unmanaged<IntPtr, void>)P("glDeleteSync");
        CreateBuffers = (delegate* unmanaged<int, uint*, void>)P("glCreateBuffers");
        CreateVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glCreateVertexArrays");
        CreateTextures = (delegate* unmanaged<uint, int, uint*, void>)P("glCreateTextures");
        NamedBufferData = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glNamedBufferData");
        NamedBufferStorage = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glNamedBufferStorage");
        NamedBufferSubData = (delegate* unmanaged<uint, IntPtr, IntPtr, void*, void>)P("glNamedBufferSubData");
        CopyNamedBufferSubData = (delegate* unmanaged<uint, uint, IntPtr, IntPtr, IntPtr, void>)P("glCopyNamedBufferSubData");
        ClearNamedBufferSubData = (delegate* unmanaged<uint, uint, IntPtr, IntPtr, uint, uint, void*, void>)P("glClearNamedBufferSubData");
        MapNamedBufferRange = (delegate* unmanaged<uint, IntPtr, IntPtr, uint, void*>)P("glMapNamedBufferRange");
        UnmapNamedBuffer = (delegate* unmanaged<uint, byte>)P("glUnmapNamedBuffer");
        TextureStorage2D = (delegate* unmanaged<uint, int, uint, int, int, void>)P("glTextureStorage2D");
        TextureStorage3D = (delegate* unmanaged<uint, int, uint, int, int, int, void>)P("glTextureStorage3D");
        TextureStorage2DMultisample = (delegate* unmanaged<uint, int, uint, int, int, byte, void>)P("glTextureStorage2DMultisample");
        TextureSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTextureSubImage2D");
        TextureSubImage3D = (delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, void*, void>)P("glTextureSubImage3D");
        CompressedTextureSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, int, void*, void>)P("glCompressedTextureSubImage2D");
        CompressedTextureSubImage3D = (delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, int, void*, void>)P("glCompressedTextureSubImage3D");
        TextureParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTextureParameteri");
        GetTextureParameteriv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetTextureParameteriv");
        GetTextureLevelParameteriv = (delegate* unmanaged<uint, int, uint, int*, void>)P("glGetTextureLevelParameteriv");
        GetInternalformativ = (delegate* unmanaged<uint, uint, uint, int, int*, void>)P("glGetInternalformativ");
        GetTextureImage = (delegate* unmanaged<uint, int, uint, uint, int, void*, void>)P("glGetTextureImage");
        GenerateTextureMipmap = (delegate* unmanaged<uint, void>)P("glGenerateTextureMipmap");
        VertexArrayVertexBuffer = (delegate* unmanaged<uint, uint, uint, IntPtr, int, void>)P("glVertexArrayVertexBuffer");
        VertexArrayAttribFormat = (delegate* unmanaged<uint, uint, int, uint, byte, uint, void>)P("glVertexArrayAttribFormat");
        VertexArrayAttribBinding = (delegate* unmanaged<uint, uint, uint, void>)P("glVertexArrayAttribBinding");
        VertexArrayBindingDivisor = (delegate* unmanaged<uint, uint, uint, void>)P("glVertexArrayBindingDivisor");
        EnableVertexArrayAttrib = (delegate* unmanaged<uint, uint, void>)P("glEnableVertexArrayAttrib");
        VertexArrayElementBuffer = (delegate* unmanaged<uint, uint, void>)P("glVertexArrayElementBuffer");
        GetVertexArrayiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetVertexArrayiv");
        GetNamedRenderbufferParameteriv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetNamedRenderbufferParameteriv");

        // FBO procs are NOT in this gate: text/chat rendering must not regress if a client lacks them.
        // SceneInputs null-checks GenFramebuffers/FramebufferTexture2D itself before using the FBO path.
        _ready = CreateProgram != null && GenVertexArrays != null && BufferData != null && DrawArrays != null;
        if (!_ready) { log.Info("Gl: a required GL 3.x proc was missing"); _failed = true; }
        else BindlessLedger.Install(log);
        return _ready;
    }

    /// <summary>Copy an ASCII name into <paramref name="dst"/>, NUL-terminated: <c>gl*</c> entry points and GLSL
    /// identifiers are ASCII, so the byte cast is exact. <paramref name="dst"/> holds <c>name.Length + 1</c>
    /// bytes, which is what lets every caller keep its buffer on the stack.</summary>
    internal static void AsciiZ(string name, Span<byte> dst)
    {
        for (int i = 0; i < name.Length; i++) dst[i] = (byte)name[i];
        dst[name.Length] = 0;
    }

    /// <summary>Resolve one entry point by name.</summary>
    internal static IntPtr P(string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        AsciiZ(name, b);
        fixed (byte* p = b) return _getProc(p);
    }
}
