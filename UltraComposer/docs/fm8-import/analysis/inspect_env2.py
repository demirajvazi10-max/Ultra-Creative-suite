import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def examine():
    # Load preset Amza MOSS 2 (len 3047), HUSNU CLARINET2017 (len 3071), and a 2987 preset
    files = sorted([f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")])
    presets = {}
    for f in files:
        raw = open(os.path.join(CORPUS_DIR, f), "rb").read()
        pos, body = nfm8_decode.find_compressed_body(raw)
        ver, hv, strs, p = nfm8_decode.parse_e8mf(body)
        presets[f] = p

    p2987 = [p for p in presets.values() if len(p) == 2987][0]
    p3047 = presets["Amza MOSS 2.nfm8"]
    p3071 = presets["HUSNU CLARINET2017.nfm8"]

    print(f"p2987 len={len(p2987)}, p3047 len={len(p3047)}, p3071 len={len(p3071)}")

    # Find the exact offset where p2987, p3047, p3071 first differ
    for i in range(min(len(p2987), len(p3047), len(p3071))):
        # We know floats can differ, but where do structural differences happen?
        pass

    # Let's inspect the entire parameter block of p3071 from offset 0 to 200
    print("\nFirst 200 bytes of p3071:")
    for i in range(0, 200, 16):
        chunk = p3071[i:i+16]
        print(f"0x{i:04x} ({i:4d}): " + " ".join(f"{b:02x}" for b in chunk))

if __name__ == "__main__":
    examine()
