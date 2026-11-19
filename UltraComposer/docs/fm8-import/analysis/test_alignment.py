import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def examine():
    path = os.path.join(CORPUS_DIR, "Amza MOSS 2.nfm8")
    raw = open(path, "rb").read()
    pos, body = nfm8_decode.find_compressed_body(raw)
    ver, hv, strs, p = nfm8_decode.parse_e8mf(body)

    # Let's inspect where 4-byte float sequences break!
    # A 32-bit float in audio synths usually has exponent between 0x70 (-16) and 0x88 (+16)
    # or is 0.0 (0x00000000).
    # If a block is an array of floats, unpacking as float gives normal numbers like 0.0 .. 100.0.
    # When alignment breaks, floats suddenly become crazy (like 1e30 or 1e-35 or NaN).
    
    # Let's check float sanity for offset 0..len(p)
    off = 0
    while off < len(p) - 4:
        val = struct.unpack_from("<f", p, off)[0]
        # is val a reasonable float or 0?
        is_normal = (val == 0.0 or (-10000.0 < val < 10000.0 and abs(val) > 1e-5))
        if not is_normal:
            print(f"First abnormal float at offset {off} (0x{off:x}): bytes={p[off:off+8].hex()}")
            break
        off += 4

if __name__ == "__main__":
    examine()
