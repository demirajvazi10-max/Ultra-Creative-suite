import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def inspect_envelopes():
    files = sorted([f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")])
    presets = []
    for f in files:
        raw = open(os.path.join(CORPUS_DIR, f), "rb").read()
        pos, body = nfm8_decode.find_compressed_body(raw)
        ver, hv, strs, p = nfm8_decode.parse_e8mf(body)
        presets.append((f, p))

    p_3071 = [p for f, p in presets if len(p) == 3071][0]
    
    # Let's inspect the bytes from 1700 to 2200 in p_3071
    print("Dumping 1740 to 2200 of p_3071:")
    for offset in range(1740, 2200, 4):
        u = struct.unpack_from("<I", p_3071, offset)[0]
        f = struct.unpack_from("<f", p_3071, offset)[0]
        print(f"0x{offset:04x} ({offset:4d}): u32={u:10d} (0x{u:08x})  float={f:12.6f}")

if __name__ == "__main__":
    inspect_envelopes()
