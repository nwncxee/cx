#!/usr/bin/env bash
# The held-buffer compiler rule's approved examples: planted grow helpers compiled against tools/analyzers/, the
# CX0011 lines the compiler printed read back. The backend, the buffer id and HeldBuffer are planted stand-ins with
# the real names, which is all the rule reads. Oracle: the planted files and the lines written by hand below. Needs
# the .NET SDK, so build.sh runs it, not tools/gates/run.sh.
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
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo/tools/analyzers/cx_analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
EOF
printf '%s\n' '[*.cs]' 'dotnet_diagnostic.CX0011.severity = error' > "$tmp/.editorconfig"
mkdir -p "$tmp/planted"

cat > "$tmp/planted/Shim.cs" <<'EOF'
namespace Planted;
public struct BufferId { public uint Value; public bool IsNull => Value == 0; }
public sealed class VkBackend
{
    public BufferId CreateBuffer(long bytes) => new() { Value = (uint)bytes };
    public void Destroy(BufferId id) { _ = id; }
}
internal static class HeldBuffer
{
    internal static bool Grow(VkBackend vk, ref BufferId id, ref long cap, long bytes)
    {
        if (!id.IsNull && bytes <= cap) return false;
        vk.Destroy(id);
        cap = bytes;
        id = vk.CreateBuffer(cap);
        return !id.IsNull;
    }
    internal static void Zeroed(VkBackend vk, ref BufferId id, ref long cap, long bytes) => Grow(vk, ref id, ref cap, bytes);
}
internal sealed class SetState
{
    public readonly BufferId[] Cmd = new BufferId[4], Cnt = new BufferId[4];
    public readonly long[] CmdCap = new long[4], CntCap = new long[4];
    public BufferId Key, Rej;
    public long KeyCap, RejCap, SharedCap;
}
EOF

# E1
# Given a pass remakes its buffers through a helper the caller tells to grow with a bool, as the cull pass did
# When the code compiles
# Then the compiler refuses the helper where it creates the buffer, naming HeldBuffer
cat > "$tmp/planted/E1.cs" <<'EOF'
namespace Planted;
internal static class E1
{
    private static void Make(VkBackend vk, ref BufferId id, bool grow, long bytes)
    {
        if (!id.IsNull && !grow) return;
        vk.Destroy(id);
        id = vk.CreateBuffer(bytes);
    }
}
EOF

# E2
# Given a pass keeps a grow helper of its own that holds each buffer's size correctly
# When the code compiles
# Then the compiler refuses it all the same: HeldBuffer is the one grow helper
cat > "$tmp/planted/E2.cs" <<'EOF'
namespace Planted;
internal static class E2
{
    private static bool Grow(VkBackend vk, ref BufferId id, ref long cap, long bytes)
    {
        if (!id.IsNull && bytes <= cap) return true;
        vk.Destroy(id);
        cap = bytes * 2;
        id = vk.CreateBuffer(cap);
        return !id.IsNull;
    }
}
EOF

# E3
# Given a loop holds each of a set's command buffers through HeldBuffer to one size field, as the caster gather did
# When the code compiles
# Then the compiler refuses the size argument
cat > "$tmp/planted/E3.cs" <<'EOF'
namespace Planted;
internal static class E3
{
    internal static void Hold(VkBackend vk, SetState s, long bytes)
    {
        for (int i = 0; i < 4; i++)
            HeldBuffer.Zeroed(vk, ref s.Cmd[i], ref s.SharedCap, bytes);
    }
}
EOF

# E4
# Given two buffers are held through HeldBuffer to one size field
# When the code compiles
# Then the compiler refuses the second
cat > "$tmp/planted/E4.cs" <<'EOF'
namespace Planted;
internal static class E4
{
    internal static void Hold(VkBackend vk, SetState s, long bytes)
    {
        HeldBuffer.Zeroed(vk, ref s.Key, ref s.KeyCap, bytes);
        HeldBuffer.Zeroed(vk, ref s.Rej, ref s.KeyCap, bytes);
    }
}
EOF

# E5
# Given every buffer is held through HeldBuffer to its own size, an array of buffers to the array of sizes beside it
# When the code compiles
# Then it compiles clean
cat > "$tmp/planted/E5.cs" <<'EOF'
namespace Planted;
internal static class E5
{
    internal static void Hold(VkBackend vk, SetState s, long bytes)
    {
        for (int i = 0; i < 4; i++)
        {
            HeldBuffer.Zeroed(vk, ref s.Cmd[i], ref s.CmdCap[i], bytes);
            HeldBuffer.Zeroed(vk, ref s.Cnt[i], ref s.CntCap[ i ], 16);
        }
        HeldBuffer.Zeroed(vk, ref s.Key, ref s.KeyCap, bytes);
        HeldBuffer.Grow(vk, ref s.Rej, ref s.RejCap, bytes);
    }
}
EOF

# E6
# Given a method that takes a buffer by reference and only frees it, and one that creates a buffer and returns it
# When the code compiles
# Then each compiles clean
cat > "$tmp/planted/E6.cs" <<'EOF'
namespace Planted;
internal static class E6
{
    internal static void Free(VkBackend vk, ref BufferId id) { vk.Destroy(id); id = default; }
    internal static BufferId Make(VkBackend vk, long bytes) => vk.CreateBuffer(bytes);
}
EOF

out=$(dotnet build "$tmp/fixture.csproj" -nologo -v q -clp:NoSummary 2>&1)
cx=$(printf '%s\n' "$out" | grep -o '[A-Za-z0-9.]*\.cs([0-9]*,[0-9]*): error CX0011.*' | sed 's# \[/.*##' | sort -u)
# The only error this fixture is MEANT to print is CX0011; anything else is the fixture's own failure.
other=$(printf '%s\n' "$out" | grep -oE 'error [A-Z]+[0-9]+' | grep -vx 'error CX0011' | sort -u)
if [ -n "$other" ] || printf '%s\n' "$out" | grep -q 'MSB9008'; then
  echo "!! held buffer approved examples: the fixture project did not restore, load or compile" \
       "(${other:-MSB9008}) -- $out" >&2; exit 2
fi
expect_line() {
  printf '%s\n' "$cx" | grep -q "^$1($2,.*$3" \
    || { echo "!! $4: no CX0011 saying '$3' at $1 line $2 -- the compiler printed: ${cx:-no CX0011 at all}"; rc=1; }
}
clean() {
  ! printf '%s\n' "$cx" | grep -q "^$1(" || { echo "!! $2: CX0011 refused $1 -- $cx"; rc=1; }
}
expect_line E1.cs 8 "HeldBuffer.Grow" "E1, the cull pass's own shape"
expect_line E2.cs 9 "HeldBuffer.Grow" "E2"
expect_line E3.cs 7 "its own size field" "E3, the caster gather's own shape"
expect_line E4.cs 7 "its own size field" "E4"
clean E5.cs "E5"
clean E6.cs "E6"
clean Shim.cs "HeldBuffer's own methods"
[ "$(printf '%s\n' "$cx" | grep -c '^E4.cs(')" = 1 ] || { echo "!! E4: not exactly one refusal in E4.cs, the second holder alone -- $cx"; rc=1; }

[ $rc = 0 ] || echo "!! held buffer approved examples: FAILED (above)" >&2
exit $rc
