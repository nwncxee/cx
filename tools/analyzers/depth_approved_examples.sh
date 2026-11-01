#!/usr/bin/env bash
# The world depth compiler rule's approved examples (approved-spec, CX-405 E5 to E7): planted passes compiled against
# tools/analyzers/, the CX0006 lines the compiler printed read back. Oracle: the planted files and the lines written
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
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo/tools/analyzers/cx_analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <Compile Include="$repo/managed/Alloy/Core/GlDepth.cs;$repo/managed/Rhi/Pipeline.cs;$repo/managed/Rhi/Descriptors.cs;$repo/managed/Rhi/Handles.cs" />
  </ItemGroup>
</Project>
EOF
printf '%s\n' '[*.cs]' 'dotnet_diagnostic.CX0006.severity = error' > "$tmp/.editorconfig"
mkdir -p "$tmp/managed/Alloy/Pass"

# E5
# Given a world pass whose depth test is GL's, less or equal
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/managed/Alloy/AlloyRenderer.VkE5.cs" <<'EOF'
using CX.Rhi;
namespace CX.Alloy;
internal static class E5
{
    internal static DepthState World() => new DepthState(true, true, CompareOp.LessEqual);
}
EOF
# Boundary: a shadow caster pass with the same test compiles
cat > "$tmp/managed/Alloy/Pass/CasterPass.Vk.cs" <<'EOF'
using CX.Rhi;
namespace CX.Alloy;
internal static class E5Caster
{
    internal static DepthState Caster() => new DepthState(true, true, GlDepth.NearerOrEqual);
}
EOF

# E6
# Given a pass that turns the depth test on and names no comparison
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/managed/Alloy/AlloyRenderer.VkE6.cs" <<'EOF'
using CX.Rhi;
namespace CX.Alloy;
internal static class E6
{
    internal static DepthState Tested() => new DepthState(true, false);
    // Boundary: a pass with the depth test off names no comparison and compiles
    internal static DepthState Untested() => new DepthState(false, false);
}
EOF

# E7
# Given a world pass that clears its depth target to 1, GL's far plane
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/managed/Alloy/AlloyRenderer.VkE7.cs" <<'EOF'
using CX.Rhi;
namespace CX.Alloy;
internal static class E7
{
    internal static DepthAttachment World(ImageId d) => new DepthAttachment(d, 0, 0, LoadOp.Clear, StoreOp.Store, 1f);
}
EOF
# Boundary: the GUI clearing its own depth to 1 compiles
cat > "$tmp/managed/Alloy/Pass/GuiPassVk.Issue.cs" <<'EOF'
using CX.Rhi;
namespace CX.Alloy;
internal static class E7Gui
{
    internal static DepthAttachment Gui(ImageId d) => new DepthAttachment(d, 0, 0, LoadOp.Clear, StoreOp.Store, GlDepth.Clear);
}
EOF

out=$(dotnet build "$tmp/fixture.csproj" -nologo -v q -clp:NoSummary 2>&1)
cx=$(printf '%s\n' "$out" | grep -o '[A-Za-z0-9.]*\.cs([0-9]*,[0-9]*): error CX0006' | sort -u)
if printf '%s\n' "$out" | grep -q 'error NU\|error MSB\|MSB9008'; then
  echo "!! depth approved examples: the fixture project did not restore or load -- $out" >&2; exit 2
fi
expect_line() {
  printf '%s\n' "$cx" | grep -q "^$1($2," || { echo "!! $3: no CX0006 at $1 line $2 -- the compiler printed: ${cx:-no CX0006 at all}"; rc=1; }
}
clean() {
  ! printf '%s\n' "$cx" | grep -q "^$1(${2:-}" || { echo "!! $3: CX0006 refused $1 ${2:+line $2} -- $cx"; rc=1; }
}
expect_line AlloyRenderer.VkE5.cs 5 "E5"
clean CasterPass.Vk.cs "" "E5 boundary"
expect_line AlloyRenderer.VkE6.cs 5 "E6"
clean AlloyRenderer.VkE6.cs 7 "E6 boundary"
expect_line AlloyRenderer.VkE7.cs 5 "E7"
clean GuiPassVk.Issue.cs "" "E7 boundary"
clean GlDepth.cs "" "GlDepth's own declarations"

[ $rc = 0 ] || echo "!! depth approved examples: FAILED (above)" >&2
exit $rc
