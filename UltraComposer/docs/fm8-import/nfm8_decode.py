#!/usr/bin/env python3
"""Reference decoder for Native Instruments FM8 presets (.nfm8).

Verified on 61 real presets (FM8 E8MF chunk version 206): every file
decompresses to exactly the size its header declares.

Usage:  python nfm8_decode.py <file.nfm8> [out.bin]
Prints the metadata strings and writes the decompressed body (optional).
"""
import struct, sys


def lzf_decompress(data, start, end):
    """Plain LZF (same scheme as liblzf). Returns bytes or raises ValueError."""
    out = bytearray()
    i = start
    while i < end:
        ctrl = data[i]; i += 1
        if ctrl < 32:                      # literal run of ctrl+1 bytes
            n = ctrl + 1
            if i + n > end:
                raise ValueError("literal past end")
            out += data[i:i + n]; i += n
        else:                              # back reference
            length = ctrl >> 5
            if length == 7:
                length += data[i]; i += 1
            ref = len(out) - ((ctrl & 0x1F) << 8) - data[i] - 1; i += 1
            if ref < 0:
                raise ValueError("bad back reference")
            for _ in range(length + 2):
                out.append(out[ref]); ref += 1
    return bytes(out)


def find_compressed_body(data):
    """The compressed preset sits behind a 9-byte header:
         u8  1
         u32 uncompressed size
         u32 compressed size
       followed by the LZF stream. We scan for a header whose stream
       decompresses to exactly the declared size."""
    for p in range(len(data) - 9):
        if data[p] != 1:
            continue
        usize, csize = struct.unpack_from("<II", data, p + 1)
        if 100 < usize < 1_000_000 and 10 < csize <= len(data) - p - 9 and csize < usize:
            try:
                body = lzf_decompress(data, p + 9, p + 9 + csize)
            except (ValueError, IndexError):
                continue
            if len(body) == usize:
                return p, body
    raise ValueError("compressed body not found")


def parse_e8mf(body):
    """Returns (version, header_value, [4 strings], param_bytes)."""
    i = body.find(b"E8MF")
    if i < 0:
        raise ValueError("no E8MF chunk")
    version, header_value = struct.unpack_from("<II", body, i + 4)
    p = i + 12
    strings = []
    for _ in range(4):                     # u32 length + latin-1 text, 4 times
        n = struct.unpack_from("<I", body, p)[0]
        strings.append(body[p + 4:p + 4 + n].decode("latin-1"))
        p += 4 + n
    return version, header_value, strings, body[p:]


if __name__ == "__main__":
    raw = open(sys.argv[1], "rb").read()
    pos, body = find_compressed_body(raw)
    ver, hv, strs, params = parse_e8mf(body)
    print(f"compressed header at 0x{pos:x}, body {len(body)} bytes")
    print(f"E8MF version {ver}, header value {hv}")
    print("strings:", strs)
    print(f"parameter block: {len(params)} bytes")
    if len(sys.argv) > 2:
        open(sys.argv[2], "wb").write(body)
