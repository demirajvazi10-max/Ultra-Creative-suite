import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def examine():
    files = sorted([f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")])
    presets = []
    for f in files:
        raw = open(os.path.join(CORPUS_DIR, f), "rb").read()
        pos, body = nfm8_decode.find_compressed_body(raw)
        ver, hv, strs, p = nfm8_decode.parse_e8mf(body)
        presets.append((f, p))

    # For each of the 214 float words (0..213):
    # compute min, max, number of unique values, sample values
    print("Word (Offset): [min, max] (num_unique) sample_values")
    for w in range(214):
        off = w * 4
        vals = [struct.unpack_from("<f", p, off)[0] for f, p in presets]
        u_vals = sorted(list(set(vals)))
        min_v = min(vals)
        max_v = max(vals)
        samples = ", ".join(f"{v:g}" for v in u_vals[:6])
        if len(u_vals) > 6:
            samples += "..."
        # if values look like ratios or integers or levels:
        print(f"W{w:3d} (0x{off:03x}): [{min_v:8.3f}, {max_v:8.3f}] ({len(u_vals):2d}) {samples}")

if __name__ == "__main__":
    examine()
