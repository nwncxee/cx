#!/usr/bin/env bash
# The sum-bound compiler rule's approved examples: planted range checks compiled against the real CX.Rhi.Bounds and
# tools/analyzers/, the CX0010 lines the compiler printed read back. Oracle: the planted files and the lines written
# by hand below. Needs the .NET SDK, so build.sh runs it, not tools/gates/run.sh.
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
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo/tools/analyzers/cx_analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <Compile Include="$repo/managed/Rhi/Bounds.cs" />
    <Compile Include="managed/**/*.cs" />
  </ItemGroup>
</Project>
EOF
printf '%s\n' '[*.cs]' 'dotnet_diagnostic.CX0010.severity = error' > "$tmp/.editorconfig"
g="$tmp/managed/Graphics"
mkdir -p "$g" "$tmp/managed/Alloy"

# E1
# Given the backend tests an indirect range as offset plus length against the buffer's bytes
# When the code compiles
# Then the compiler refuses it at that line, naming Bounds
cat > "$g/E1.cs" <<'EOF'
namespace Planted;
internal static class E1
{
    internal static bool Fits(long offset, long length, long bytes) => offset >= 0 && !(offset + length > bytes);
}
EOF

# E2
# Given a staging block tests its room as used plus length against its size, unsigned
# When the code compiles
# Then the compiler refuses it at that line
cat > "$g/E2.cs" <<'EOF'
namespace Planted;
internal static class E2
{
    internal static bool Full(ulong used, ulong len, ulong size) => used + len > size;
}
EOF

# E3
# Given the same test written the other way round, with the sum on the right and a cast around it
# When the code compiles
# Then the compiler refuses it at that line
cat > "$g/E3.cs" <<'EOF'
namespace Planted;
internal static class E3
{
    internal static bool Past(int at, int n, long size) => size < (long)(at + n);
}
EOF

# E4
# Given the range is tested by subtraction through Bounds
# When the code compiles
# Then it compiles clean
cat > "$g/E4.cs" <<'EOF'
using CX.Rhi;
namespace Planted;
internal static class E4
{
    internal static bool Fits(long offset, long length, long bytes) => Bounds.Inside(offset, length, bytes);
    internal static bool Words(long offset, long length, long bytes) => Bounds.InWords(offset, length, bytes);
}
EOF

# E5
# Given a sum that is no range: a float sum, a sum the compiler folds, and a sum tested for equality
# When the code compiles
# Then each compiles clean
cat > "$g/E5.cs" <<'EOF'
namespace Planted;
internal static class E5
{
    private const int A = 3, B = 4;
    internal static bool Far(float x, float w, float edge) => x + w > edge;
    internal static bool Folded() => A + B > 6;
    internal static bool None(int colours, int depth) => colours + depth == 0;
}
EOF

# E6
# Given the same sum outside the backend, and in a self-check that plants it
# When the code compiles
# Then each compiles clean
cat > "$tmp/managed/Alloy/E6.cs" <<'EOF'
namespace Planted;
internal static class E6
{
    internal static bool Past(int at, int n, int size) => at + n > size;
}
EOF
cat > "$g/E6.SelfCheck.cs" <<'EOF'
namespace Planted;
internal static class E6SelfCheck
{
    internal static bool Past(int at, int n, int size) => at + n > size;
}
EOF

# E7
# Given the sum is held in a local first and the local is tested against the buffer's bytes, as the indirect draw did
# When the code compiles
# Then the compiler refuses the test at its line
cat > "$g/E7.cs" <<'EOF'
namespace Planted;
internal static class E7
{
    internal static bool Fits(long offset, int draws, uint stride, long bytes)
    {
        long end = offset + (long)draws * stride;
        return offset >= 0 && end <= bytes;
    }
}
EOF

# E8
# Given a sum tested against zero, which is a sign test and has no size to stay inside
# When the code compiles
# Then it compiles clean
cat > "$g/E8.cs" <<'EOF'
namespace Planted;
internal static class E8
{
    internal static bool Found(int dash)
    {
        int gpu = dash + 1;
        return gpu > 0;
    }
}
EOF

out=$(cd "$tmp" && dotnet build fixture.csproj -nologo -v q -clp:NoSummary 2>&1)
cx=$(printf '%s\n' "$out" | grep -o '[A-Za-z0-9.]*\.cs([0-9]*,[0-9]*): error CX0010[^[]*' | sort -u)
# The only error this fixture is MEANT to print is CX0010; anything else is the fixture's own failure.
other=$(printf '%s\n' "$out" | grep -oE 'error [A-Z]+[0-9]+' | grep -vx 'error CX0010' | sort -u)
if [ -n "$other" ] || printf '%s\n' "$out" | grep -q 'MSB9008'; then
  echo "!! sum bound approved examples: the fixture project did not restore, load or compile" \
       "(${other:-MSB9008}) -- $out" >&2; exit 2
fi
expect_line() {
  printf '%s\n' "$cx" | grep -q "^$1($2,.*Bounds.Inside" \
    || { echo "!! $3: no CX0010 naming Bounds at $1 line $2 -- the compiler printed: ${cx:-no CX0010 at all}"; rc=1; }
}
clean() {
  ! printf '%s\n' "$cx" | grep -q "^$1(" || { echo "!! $2: CX0010 refused $1 -- $cx"; rc=1; }
}
expect_line E1.cs 4 "E1"
expect_line E2.cs 4 "E2"
expect_line E3.cs 4 "E3"
expect_line E7.cs 7 "E7, the indirect draw's own shape"
clean E8.cs "E8"
clean E4.cs "E4"
clean E5.cs "E5"
clean E6.cs "E6 outside the backend"
clean E6.SelfCheck.cs "E6 in a self-check"
clean Bounds.cs "Bounds' own declaration"

[ $rc = 0 ] || echo "!! sum bound approved examples: FAILED (above)" >&2
exit $rc
