#!/usr/bin/env bash
# Build VK_LAYER_CX_passtime into build/passtime/ (the .so and its manifest side by side) after its self-check.
# Linux only. Use: VK_ADD_LAYER_PATH=build/passtime VK_INSTANCE_LAYERS=VK_LAYER_CX_passtime PASSTIME_OUT=<file>,
# or tools/live/perf-save.sh --passtime.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/tools/gpu-passtime"
OUT="$ROOT/build/passtime"
CC="${CC:-cc}"
mkdir -p "$OUT"
"$CC" -std=c11 -O1 -Wall -Wextra -Werror -Wno-format-truncation -o "$OUT/passtime-test" "$SRC/test.c" -lm
"$OUT/passtime-test"
"$CC" -std=c11 -O2 -Wall -Wextra -Werror -Wno-format-truncation -fPIC -shared -fvisibility=hidden -o "$OUT/libcx_passtime.so" "$SRC/passtime.c" -lpthread
cat > "$OUT/VkLayer_CX_passtime.json" <<'EOF'
{
  "file_format_version": "1.1.2",
  "layer": {
    "name": "VK_LAYER_CX_passtime",
    "type": "GLOBAL",
    "library_path": "./libcx_passtime.so",
    "api_version": "1.3.0",
    "implementation_version": "1",
    "description": "GPU time per debug-utils label on live frames (NWNCX:EE measuring tool)"
  }
}
EOF
echo "passtime: built $OUT"
