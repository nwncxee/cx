using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>Three floats laid end to end: a vector, a colour. <c>((Stride3f*)p)[i].F</c> is the i-th one's address.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Stride3f { public fixed float F[3]; }

/// <summary>Four floats laid end to end: a sphere (centre, radius), a plane, a quaternion.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Stride4f { public fixed float F[4]; }

/// <summary>Six floats laid end to end: a box (min, max).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Stride6f { public fixed float F[6]; }

/// <summary>Nine floats laid end to end: a triangle's corners.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Stride9f { public fixed float F[9]; }

/// <summary>Four ints laid end to end: one draw's engine texture units.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Stride4i { public fixed int F[4]; }

/// <summary>Sixteen floats laid end to end: one matrix.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Stride16f { public fixed float F[16]; }

/// <summary>Eight 64-bit words laid end to end: one record's resident texture handles.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Stride8u { public fixed ulong F[8]; }
