#!/usr/bin/env python3
"""The FSR 3.1 upscaler's Vulkan shader permutation headers, as AMD's FidelityFX_SC writes them for the GLSL path.

    tools/ffx/ffx_shaders.py <glslang> <sdk dir> <out dir>

FidelityFX_SC is a Windows program; this writes the same headers from glslangValidator and a reflection of the
SPIR-V it emits, so both build hosts share one generator. Only the base set (wave32, fp32) is compiled: the
wave64 and 16-bit names alias it, because native/ffx/cxfsr.cpp reports no extension to the backend and so it never
picks them, and only the keys CXFSR_FLAGS reaches (KEYS below).
"""
import hashlib, os, pathlib, struct, subprocess, sys
from concurrent.futures import ThreadPoolExecutor

OPTIONS = ['FFX_FSR3UPSCALER_OPTION_REPROJECT_USE_LANCZOS_TYPE', 'FFX_FSR3UPSCALER_OPTION_HDR_COLOR_INPUT',
           'FFX_FSR3UPSCALER_OPTION_LOW_RESOLUTION_MOTION_VECTORS', 'FFX_FSR3UPSCALER_OPTION_JITTERED_MOTION_VECTORS',
           'FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH', 'FFX_FSR3UPSCALER_OPTION_APPLY_SHARPENING']
BASE = ['-V', '--target-env', 'vulkan1.2', '-S', 'comp', '-Os', '-DFFX_GLSL=1', '-DFFX_GPU=1', '-DFFX_HALF=0',
        '-DFFX_FSR3UPSCALER_OPTION_UPSAMPLE_SAMPLERS_USE_DATA_HALF=0',
        '-DFFX_FSR3UPSCALER_OPTION_ACCUMULATE_SAMPLERS_USE_DATA_HALF=0',
        '-DFFX_FSR3UPSCALER_OPTION_REPROJECT_SAMPLERS_USE_DATA_HALF=1',
        '-DFFX_FSR3UPSCALER_OPTION_POSTPROCESSLOCKSTATUS_SAMPLERS_USE_DATA_HALF=0',
        '-DFFX_FSR3UPSCALER_OPTION_UPSAMPLE_USE_LANCZOS_TYPE=2']
# The options native/ffx/cxfsr.cpp's CXFSR_FLAGS fix at context creation: HDR colour, render-size and unjittered motion,
# inverted depth. Only Lanczos and sharpening vary per pass, so 4 of the 64 keys are reachable. The rest index an empty
# entry: a flag change there fails FSR's context creation, which CX refuses loudly, instead of running a wrong shader.
FIXED = {'FFX_FSR3UPSCALER_OPTION_HDR_COLOR_INPUT': 1, 'FFX_FSR3UPSCALER_OPTION_LOW_RESOLUTION_MOTION_VECTORS': 1,
         'FFX_FSR3UPSCALER_OPTION_JITTERED_MOTION_VECTORS': 0, 'FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH': 1}
KEYS = [k for k in range(1 << len(OPTIONS)) if all((k >> OPTIONS.index(o)) & 1 == v for o, v in FIXED.items())]
VARIANTS = ['_wave64', '_16bit', '_wave64_16bit']
KINDS = ['CBV', 'TextureSRV', 'TextureUAV', 'BufferSRV', 'BufferUAV', 'Sampler', 'RTAccelerationStructure']


def reflect(spv):
    """Each descriptor binding as (kind, name, binding, count, set), sorted by set then binding."""
    w = struct.unpack(f'<{len(spv) // 4}I', spv)
    names, deco, types, consts, members_nw, vars_ = {}, {}, {}, {}, {}, []
    i = 5
    while i < len(w):
        op, n = w[i] & 0xffff, w[i] >> 16
        a = w[i + 1:i + n]
        if op == 5:  # OpName
            names[a[0]] = bytes(struct.pack(f'<{n - 2}I', *a[1:])).split(b'\0')[0].decode()
        elif op == 71:  # OpDecorate
            deco.setdefault(a[0], {})[a[1]] = a[2] if len(a) > 2 else True
        elif op == 72 and a[2] == 24:  # OpMemberDecorate NonWritable
            members_nw.setdefault(a[0], set()).add(a[1])
        elif op in (25, 26, 27, 28, 29, 30, 32):  # image, sampler, sampled image, array, runtime array, struct, pointer
            types[a[0]] = (op, a[1:])
        elif op == 43:  # OpConstant
            consts[a[1]] = a[2]
        elif op == 59:  # OpVariable
            vars_.append((a[1], a[0], a[2]))
        i += n
    out = []
    for vid, ptr, storage in vars_:
        if 33 not in deco.get(vid, {}):
            continue
        t = types[ptr][1][1]
        count = 1
        while types[t][0] in (28, 29):
            count = consts[types[t][1][1]] if types[t][0] == 28 else 0
            t = types[t][1][0]
        op, rest = types[t]
        if op == 25:
            kind = 'TextureUAV' if rest[5] == 2 else 'TextureSRV'
        elif op == 26:
            kind = 'Sampler'
        elif op == 30 and storage == 2 and 2 in deco.get(t, {}):  # Uniform + Block
            kind = 'CBV'
        elif op == 30 and (storage == 12 or 3 in deco.get(t, {})):  # StorageBuffer, or Uniform + BufferBlock
            kind = 'BufferUAV' if len(members_nw.get(t, ())) < len(rest) else 'BufferSRV'
        else:
            sys.exit(f'!! unsupported descriptor type (op {op}) for {names.get(vid)}')
        out.append((kind, names.get(vid) or names.get(t, ''), deco[vid][33], count, deco[vid].get(34, 0)))
    return sorted(out, key=lambda r: (r[4], r[2]))


def compile_pass(glslang, sdk, src, out, key):
    gpu = sdk / 'include/FidelityFX/gpu'
    tmp = out / f'{src.stem}_{key}.spv'
    defs = [f'-D{o}={(key >> b) & 1}' for b, o in enumerate(OPTIONS)]
    r = subprocess.run([glslang, *BASE, *defs, f'-I{gpu}', f'-I{gpu / "fsr3upscaler"}', '-o', str(tmp), str(src)],
                       capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f'!! {src.name} permutation {key}:\n{r.stdout}{r.stderr}')
    spv = tmp.read_bytes()
    tmp.unlink()
    return spv


def write_pass(name, perms, out):
    unique, index = {}, [0] * (1 << len(OPTIONS))
    for key, spv in perms.items():
        digest = hashlib.md5(spv).hexdigest()
        unique.setdefault(digest, spv)
        index[key] = list(unique).index(digest) + 1
    for digest, spv in unique.items():
        pn = f'{name}_{digest}'
        lines = [f'// {pn}.h, generated by tools/ffx/ffx_shaders.py.\n#pragma once\n']
        for kind in KINDS:
            rows = [r for r in reflect(spv) if r[0] == kind]
            if rows:
                lines.append(f'static const char* g_{pn}_{kind}ResourceNames[] = {{ ' + ', '.join(f'"{r[1]}"' for r in rows) + ' };')
                for field, col in (('Bindings', 2), ('Counts', 3), ('Sets', 4)):
                    lines.append(f'static const uint32_t g_{pn}_{kind}Resource{field}[] = {{ ' + ', '.join(str(r[col]) for r in rows) + ' };')
        lines.append(f'\nstatic const uint32_t g_{pn}_size = {len(spv)};\n')
        body = ','.join(f'0x{b:02x}' for b in spv)
        lines.append(f'static const unsigned char g_{pn}_data[] = {{\n{body}\n}};\n')
        (out / f'{pn}.h').write_text('\n'.join(lines))
    hdr = ['#pragma once'] + [f'#include "{name}_{d}.h"' for d in unique] + ['', f'typedef union {name}_PermutationKey {{', '    struct {']
    hdr += [f'        uint32_t {o} : 1;' for o in OPTIONS] + ['    };', '    uint32_t index;', f'}} {name}_PermutationKey;', '']
    hdr += [f'typedef struct {name}_PermutationInfo {{', '    const uint32_t       blobSize;', '    const unsigned char* blobData;', '']
    for field, cap in (('constantBuffer', 'ConstantBuffers'), ('srvTexture', 'SRVTextures'), ('uavTexture', 'UAVTextures'),
                       ('srvBuffer', 'SRVBuffers'), ('uavBuffer', 'UAVBuffers'), ('sampler', 'Samplers'),
                       ('rtAccelerationStructure', 'RTAccelerationStructures')):
        hdr += [f'    const uint32_t  num{cap};', f'    const char**    {field}Names;', f'    const uint32_t* {field}Bindings;',
                f'    const uint32_t* {field}Counts;', f'    const uint32_t* {field}Spaces;', '']
    hdr += [f'}} {name}_PermutationInfo;', '', f'static const uint32_t g_{name}_IndirectionTable[] = {{']
    hdr += [f'    {i},' for i in index] + ['};', '', f'static const {name}_PermutationInfo g_{name}_PermutationInfo[] = {{']
    hdr.append('    { 0, 0, ' + ', '.join(['0, 0, 0, 0, 0'] * len(KINDS)) + ' },')
    for digest, spv in unique.items():
        pn = f'{name}_{digest}'
        refl = reflect(spv)
        cells = []
        for kind in KINDS:
            n = sum(1 for r in refl if r[0] == kind)
            cells.append('0, 0, 0, 0, 0' if n == 0 else
                         f'{n}, g_{pn}_{kind}ResourceNames, g_{pn}_{kind}ResourceBindings, g_{pn}_{kind}ResourceCounts, g_{pn}_{kind}ResourceSets')
        hdr.append(f'    {{ g_{pn}_size, g_{pn}_data, ' + ', '.join(cells) + ' },')
    hdr += ['};', '']
    (out / f'{name}_permutations.h').write_text('\n'.join(hdr))
    for v in VARIANTS:
        (out / f'{name}{v}_permutations.h').write_text(
            f'#include "{name}_permutations.h"\n'
            f'#define g_{name}{v}_IndirectionTable g_{name}_IndirectionTable\n'
            f'#define g_{name}{v}_PermutationInfo g_{name}_PermutationInfo\n')


def main():
    glslang, sdk, out = sys.argv[1], pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3])
    out.mkdir(parents=True, exist_ok=True)
    srcs = sorted((sdk / 'src/backends/vk/shaders/fsr3upscaler').glob('*.glsl'))
    with ThreadPoolExecutor(os.cpu_count()) as pool:
        jobs = {s: {k: pool.submit(compile_pass, glslang, sdk, s, out, k) for k in KEYS} for s in srcs}
        for s, fs in jobs.items():
            write_pass(s.stem, {k: f.result() for k, f in fs.items()}, out)


if __name__ == '__main__':
    main()
