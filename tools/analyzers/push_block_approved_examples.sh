#!/usr/bin/env bash
# The push-block compiler rule's approved examples (CX-391): planted pushes compiled against the real
# CX.Rhi.CommandList and tools/analyzers/, the CX0007 lines the compiler printed read back. Oracle: the planted
# files and the lines written by hand below. Needs the .NET SDK, so build.sh runs it, not tools/gates/run.sh.
cd "$(dirname "$0")/../.."
repo=$PWD
rc=0
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

cat > "$tmp/fixture.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo/tools/analyzers/cx_analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <Compile Include="$repo/managed/Rhi/CommandList.cs;$repo/managed/Rhi/Pipeline.cs;$repo/managed/Rhi/Descriptors.cs;$repo/managed/Rhi/Handles.cs;$repo/managed/Rhi/Device.cs;$repo/managed/Rhi/PipeFeatures.cs" />
  </ItemGroup>
</Project>
EOF
printf '%s\n' '[*.cs]' 'dotnet_diagnostic.CX0007.severity = error' > "$tmp/.editorconfig"
mkdir -p "$tmp/planted"

# The one thing CX.Rhi asks of the framework, so the real CommandList compiles on its own.
cat > "$tmp/planted/Shim.cs" <<'EOF'
namespace CX.Framework;
public sealed class ExtenderLog
{
    public void Always(string line) { _ = line; }
}
EOF

# E1
# Given a push whose data is a list of byte literals written at the call site
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/planted/E1.cs" <<'EOF'
using CX.Rhi;
namespace Planted;
internal static class E1
{
    internal static void Push(CommandList cl) => cl.PushConstants(0, [1, 0, 0, 0]);
}
EOF

# E2
# Given a push whose data is the bytes of a list of int literals
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/planted/E2.cs" <<'EOF'
using System;
using System.Runtime.InteropServices;
using CX.Rhi;
namespace Planted;
internal static class E2
{
    internal static void Push(CommandList cl)
        => cl.PushConstants(0, MemoryMarshal.AsBytes((ReadOnlySpan<int>)[7, 9]));
}
EOF

# E3
# Given a push whose data is the bytes of a lone float
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/planted/E3.cs" <<'EOF'
using System;
using System.Runtime.InteropServices;
using CX.Rhi;
namespace Planted;
internal static class E3
{
    private struct Block { public float Cut; }
    internal static void Loose(CommandList cl, float cut)
        => cl.PushConstants(4, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(in cut)));
    // Boundary: the same four bytes staged in a named struct compile
    internal static void Named(CommandList cl, float cut)
    {
        var b = new Block { Cut = cut };
        cl.PushConstants(4, MemoryMarshal.AsBytes(new ReadOnlySpan<Block>(in b)));
    }
}
EOF

# Boundary: bytes this rule cannot read -- a span handed over by something else -- compile
cat > "$tmp/planted/E4.cs" <<'EOF'
using System;
using CX.Rhi;
namespace Planted;
internal static class E4
{
    internal static void Push(CommandList cl, ReadOnlySpan<byte> staged) => cl.PushConstants(0, staged);
}
EOF

out=$(dotnet build "$tmp/fixture.csproj" -nologo -v q -clp:NoSummary 2>&1)
cx=$(printf '%s\n' "$out" | grep -o '[A-Za-z0-9.]*\.cs([0-9]*,[0-9]*): error CX0007' | sort -u)
if printf '%s\n' "$out" | grep -q 'error NU\|error MSB\|MSB9008\|error CS'; then
  echo "!! push block approved examples: the fixture project did not restore, load or compile -- $out" >&2; exit 2
fi
expect_line() {
  printf '%s\n' "$cx" | grep -q "^$1($2," || { echo "!! $3: no CX0007 at $1 line $2 -- the compiler printed: ${cx:-no CX0007 at all}"; rc=1; }
}
clean() {
  ! printf '%s\n' "$cx" | grep -q "^$1(${2:-}" || { echo "!! $3: CX0007 refused $1 ${2:+line $2} -- $cx"; rc=1; }
}
expect_line E1.cs 5 "E1"
expect_line E2.cs 8 "E2"
expect_line E3.cs 9 "E3"
clean E3.cs 14 "E3 boundary"
clean E4.cs "" "E4 boundary"
clean CommandList.cs "" "the command list's own declaration"

[ $rc = 0 ] || echo "!! push block approved examples: FAILED (above)" >&2
exit $rc
