import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def find_shifts():
    files = sorted([f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")])
    presets = []
    for f in files:
        raw = open(os.path.join(CORPUS_DIR, f), "rb").read()
        pos, body = nfm8_decode.find_compressed_body(raw)
        ver, hv, strs, p = nfm8_decode.parse_e8mf(body)
        presets.append((f, p))

    # Compare the 12 presets of length 3071: they have NO length difference between each other.
    # Where are the envelope breakpoint counts in p3071?
    p3071 = [p for f, p in presets if len(p) == 3071]
    
    # Let's inspect where in p3071 all 12 presets have integers!
    # If a field is an envelope point count, it should be an integer in all 12 presets!
    # Let's find every offset in p3071 where all 12 presets have an integer value between 2 and 50.
    common_ints = []
    for off in range(0, 3071 - 4, 4):
        vals = [struct.unpack_from("<I", p, off)[0] for p in p3071]
        flts = [struct.unpack_from("<f", p, off)[0] for p in p3071]
        if all(1 <= v <= 64 for v in vals) and all(flt < 1e-30 for flt in flts):
            common_ints.append((off, vals[0], set(vals)))

    print("Offsets in 3071 presets where all 12 presets have integer in [1..64]:")
    for off, v0, s in common_ints:
        print(f"  Offset 0x{off:04x} ({off:4d}): values={s}")

if __name__ == "__main__":
    find_shifts()
