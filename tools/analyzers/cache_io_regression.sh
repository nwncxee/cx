#!/usr/bin/env bash
# CX0009: under managed/Alloy and managed/Graphics a file read whole or written goes through CacheFile. Planted files
# compiled against tools/analyzers/, the CX0009 lines the compiler printed read back. Oracle: the lines written by hand
# below. Needs the .NET SDK, so build.sh runs it (through tools/approved-examples.sh), not tools/gates/run.sh.
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
printf '%s\n' '[*.cs]' 'dotnet_diagnostic.CX0009.severity = error' > "$tmp/.editorconfig"
mkdir -p "$tmp/managed/Alloy/Pass" "$tmp/managed/Graphics" "$tmp/managed/Ui"

cat > "$tmp/managed/Alloy/Pass/Leak.cs" <<'EOF'
using System.IO;
namespace CX.Alloy;
internal static class Leak
{
    internal static void Write(string p, byte[] b) => File.WriteAllBytes(p, b);
    internal static byte[] Read(string p) => File.ReadAllBytes(p);
    internal static Stream Made(string p) => new FileStream(p, FileMode.Create);
    internal static Stream Opened(string p) => File.Open(p, FileMode.Create);
    internal static void Handle(string p) { using var h = File.OpenHandle(p, FileMode.Open, FileAccess.Write); RandomAccess.Write(h, new byte[1], 0); }
    internal static void Moved(string a, string b) => File.Copy(a, b);
    // Boundary: reads that never write
    internal static Stream Reads(string p) => new FileStream(p, FileMode.Open, FileAccess.Read);
    internal static void ReadHandle(string p) { using var h = File.OpenHandle(p, FileMode.Open, FileAccess.Read); }
    internal static string Text(string p) => File.ReadAllText(p);
    internal static Stream OpenNoAccess(string p) => File.Open(p, FileMode.Open);
    internal static Stream Info(string p) => new FileInfo(p).OpenWrite();
    internal static void InfoMove(string a, string b) => new FileInfo(a).MoveTo(b);
    internal static void Replaced(string a, string b) => File.Replace(a, b, null);
    internal static Stream InfoRead(string p) => new FileInfo(p).Open(FileMode.Open, FileAccess.Read);
}
EOF
cat > "$tmp/managed/Graphics/CacheFile.cs" <<'EOF'
using System.IO;
namespace CX.Graphics;
internal static class CacheFile { internal static void Write(string p, byte[] b) => File.WriteAllBytes(p, b); }
EOF
cat > "$tmp/managed/Alloy/Pass/Leak.SelfCheck.cs" <<'EOF'
using System.IO;
namespace CX.Alloy;
internal static class LeakCheck { internal static void Plant(string p) => File.WriteAllBytes(p, new byte[1]); }
EOF
cat > "$tmp/managed/Ui/Fonts.cs" <<'EOF'
using System.IO;
namespace CX.Ui;
internal static class Fonts { internal static byte[] Read(string p) => File.ReadAllBytes(p); }
EOF

out=$(dotnet build "$tmp/fixture.csproj" -nologo -v q -clp:NoSummary 2>&1)
if printf '%s\n' "$out" | grep -q 'error NU\|error MSB\|MSB9008'; then
  echo "!! cache io regression: the fixture project did not restore or load -- $out" >&2; exit 2
fi
cx=$(printf '%s\n' "$out" | grep -o '[A-Za-z0-9.]*\.cs([0-9]*,[0-9]*): error CX0009' | sort -u)
expect_line() {
  printf '%s\n' "$cx" | grep -q "^$1($2," || { echo "!! $3: no CX0009 at $1 line $2 -- the compiler printed: ${cx:-no CX0009 at all}"; rc=1; }
}
clean() {
  ! printf '%s\n' "$cx" | grep -q "^$1(${2:-}" || { echo "!! $3: CX0009 refused $1 ${2:+line $2} -- $cx"; rc=1; }
}
expect_line Leak.cs 5 "WriteAllBytes in a pass"
expect_line Leak.cs 6 "ReadAllBytes in a pass"
expect_line Leak.cs 7 "a FileStream with no FileAccess, made to create"
expect_line Leak.cs 8 "File.Open with FileMode.Create"
expect_line Leak.cs 9 "OpenHandle for write and RandomAccess.Write"
expect_line Leak.cs 10 "File.Copy into a folder"
clean Leak.cs 12 "a FileStream opened to read"
clean Leak.cs 13 "OpenHandle opened to read"
clean Leak.cs 14 "ReadAllText"
expect_line Leak.cs 15 "File.Open with no FileAccess, which opens read-write"
expect_line Leak.cs 16 "FileInfo.OpenWrite"
expect_line Leak.cs 17 "FileInfo.MoveTo"
expect_line Leak.cs 18 "File.Replace"
clean Leak.cs 19 "FileInfo.Open to read"
clean CacheFile.cs "" "CacheFile itself"
clean Leak.SelfCheck.cs "" "a self-check"
clean Fonts.cs "" "a file outside Alloy and Graphics"

[ $rc = 0 ] || echo "!! cache io regression: FAILED (above)" >&2
exit $rc
