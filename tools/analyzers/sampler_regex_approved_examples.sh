#!/usr/bin/env bash
# The sampler-regex compiler rule's approved examples: planted files compiled against tools/analyzers/, the CX0008
# lines the compiler printed read back. Oracle: the planted files and the lines written by hand below.
# Needs the .NET SDK, so build.sh runs it, not tools/gates/run.sh.
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
printf '%s\n' '[*.cs]' 'dotnet_diagnostic.CX0008.severity = error' > "$tmp/.editorconfig"
mkdir -p "$tmp/managed/Alloy/Core" "$tmp/managed/Alloy/Pass" "$tmp/managed/Graphics/Vulkan"

# E1
# Given a pass outside the reader with a regex literal naming uniform and sampler together
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/managed/Alloy/Pass/E1Pass.cs" <<'EOF'
using System.Text.RegularExpressions;
namespace CX.Alloy;
internal static class E1
{
    internal static bool Declared(string fs, string nm) => Regex.IsMatch(fs, $@"uniform sampler2D {nm};");
    // Boundary: a pattern naming uniform and no opaque type reads something else and compiles
    internal static bool Loose(string fs) => Regex.IsMatch(fs, @"^[ \t]*uniform\s+float\s+\w+;");
}
EOF

# E2
# Given the same literal in the reader's own file, and in a file CX0008's Allowed names
# When the code compiles
# Then no diagnostic
cat > "$tmp/managed/Graphics/Vulkan/VkGlsl.Fixture.cs" <<'EOF'
using System.Text.RegularExpressions;
namespace CX.Graphics.Rhi;
internal static class E2Home
{
    internal static readonly Regex Decl = new(@"uniform\s+(sampler|image)\w*\s+(\w+);");
}
EOF
cat > "$tmp/managed/Alloy/Core/ContentStage.Text.cs" <<'EOF'
using System.Text.RegularExpressions;
namespace CX.Alloy;
internal static class E2Allowed
{
    internal static readonly Regex Decl = new(@"uniform\s+(sampler|image)\w*\s+(\w+);");
}
EOF

# E3
# Given the same pattern on a GeneratedRegex attribute outside the reader
# When the code compiles
# Then the compiler refuses it at that line
cat > "$tmp/managed/Alloy/Pass/E3Pass.cs" <<'EOF'
using System.Text.RegularExpressions;
namespace CX.Alloy;
internal static partial class E3
{
    [GeneratedRegex(@"uniform\s+sampler2D\s+(\w+);")]
    private static partial Regex Decl();
}
EOF

out=$(dotnet build "$tmp/fixture.csproj" -nologo -v q -clp:NoSummary 2>&1)
if printf '%s\n' "$out" | grep -q 'error NU\|error MSB\|MSB9008'; then
  echo "!! sampler regex approved examples: the fixture project did not restore or load -- $out" >&2; exit 2
fi
cx=$(printf '%s\n' "$out" | grep -o '[A-Za-z0-9.]*\.cs([0-9]*,[0-9]*): error CX0008' | sort -u)
expect_line() {
  printf '%s\n' "$cx" | grep -q "^$1($2," || { echo "!! $3: no CX0008 at $1 line $2 -- the compiler printed: ${cx:-no CX0008 at all}"; rc=1; }
}
clean() {
  ! printf '%s\n' "$cx" | grep -q "^$1(${2:-}" || { echo "!! $3: CX0008 refused $1 ${2:+line $2} -- $cx"; rc=1; }
}
expect_line E1Pass.cs 5 "E1"
clean E1Pass.cs 7 "E1 boundary"
clean VkGlsl.Fixture.cs "" "E2 the reader's own file"
clean ContentStage.Text.cs "" "E2 a rewriting site"
expect_line E3Pass.cs 5 "E3"

[ $rc = 0 ] || echo "!! sampler regex approved examples: FAILED (above)" >&2
exit $rc
