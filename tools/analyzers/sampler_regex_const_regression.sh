#!/usr/bin/env bash
# CX0008 reads the FOLDED pattern, not the text at the call: a pattern held in a const named at the call site
# reached the rule as the name "Decl", which names no declaration and read clean. Planted files compiled against
# tools/analyzers/, the CX0008 lines the compiler printed read back. Oracle: the lines written by hand below.
# Needs the .NET SDK, so build.sh runs it (through tools/approved-examples.sh), not tools/gates/run.sh.
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
mkdir -p "$tmp/managed/Alloy/Pass" "$tmp/managed/Graphics/Vulkan"

# A pass outside the reader holding the pattern in a const, named at a constructor, at IsMatch and at the generator
cat > "$tmp/managed/Alloy/Pass/ConstPass.cs" <<'EOF'
using System.Text.RegularExpressions;
namespace CX.Alloy;
internal static partial class ConstReader
{
    private const string Decl = @"uniform\s+sampler2D\s+(\w+);";
    private const string Half = @"uniform\s+";
    internal static readonly Regex Made = new(Decl);
    internal static bool Declared(string fs) => Regex.IsMatch(fs, Decl);
    internal static bool Joined(string fs) => Regex.IsMatch(fs, Half + @"sampler2D\s+(\w+);");
    // Boundary: a const naming no opaque type beside the storage word reads something else
    internal static bool Loose(string fs) => Regex.IsMatch(fs, Half + @"float\s+\w+;");
    [GeneratedRegex(Decl)]
    private static partial Regex Gen();
}
EOF

# ...and the same const in the reader's own file, which is where the pattern belongs
cat > "$tmp/managed/Graphics/Vulkan/VkGlsl.Fixture.cs" <<'EOF'
using System.Text.RegularExpressions;
namespace CX.Graphics.Rhi;
internal static class ConstHome
{
    private const string Decl = @"uniform\s+sampler2D\s+(\w+);";
    internal static bool Declared(string fs) => Regex.IsMatch(fs, Decl);
}
EOF

out=$(dotnet build "$tmp/fixture.csproj" -nologo -v q -clp:NoSummary 2>&1)
if printf '%s\n' "$out" | grep -q 'error NU\|error MSB\|MSB9008'; then
  echo "!! sampler regex const regression: the fixture project did not restore or load -- $out" >&2; exit 2
fi
cx=$(printf '%s\n' "$out" | grep -o '[A-Za-z0-9.]*\.cs([0-9]*,[0-9]*): error CX0008' | sort -u)
expect_line() {
  printf '%s\n' "$cx" | grep -q "^$1($2," || { echo "!! $3: no CX0008 at $1 line $2 -- the compiler printed: ${cx:-no CX0008 at all}"; rc=1; }
}
clean() {
  ! printf '%s\n' "$cx" | grep -q "^$1(${2:-}" || { echo "!! $3: CX0008 refused $1 ${2:+line $2} -- $cx"; rc=1; }
}
expect_line ConstPass.cs 7 "a const at the constructor"
expect_line ConstPass.cs 8 "a const at IsMatch"
expect_line ConstPass.cs 9 "two consts joined at IsMatch"
clean ConstPass.cs 11 "the boundary const"
expect_line ConstPass.cs 12 "a const at the generator"
clean VkGlsl.Fixture.cs "" "the reader's own file"

[ $rc = 0 ] || echo "!! sampler regex const regression: FAILED (above)" >&2
exit $rc
