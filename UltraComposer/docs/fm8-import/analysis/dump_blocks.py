import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def examine():
    files = sorted([f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")])
    presets = []
    for f in files:
        path = os.path.join(CORPUS_DIR, f)
        raw = open(path, "rb").read()
        pos, body = nfm8_decode.find_compressed_body(raw)
        ver, hv, strs, p = nfm8_decode.parse_e8mf(body)
        presets.append((f, strs, p))

    # Look at the 8 presets that have exactly 3071 bytes
    p3071 = [p for p in presets if len(p[2]) == 3071]
    print(f"Number of 3071 presets: {len(p3071)}")
    
    # Compare their parameter blocks
    first_p = p3071[0][2]
    print(f"Comparing 3071 presets against {p3071[0][0]}:")
    diff_indices = set()
    for name, s, data in p3071[1:]:
        for i in range(len(data)):
            if data[i] != first_p[i]:
                diff_indices.add(i)
    print(f"Total diff bytes among 3071 presets: {len(diff_indices)} / {len(first_p)}")
    
    # Let's see which 4-byte aligned words change and which stay constant
    words_total = len(first_p) // 4
    words_changing = set(i // 4 for i in diff_indices)
    print(f"Total 4-byte words: {words_total}, changing words: {len(words_changing)}")

    # Let's inspect the first 30 words of first_p:
    print("\nFirst 40 words of first_p:")
    for w in range(40):
        b = first_p[w*4:(w+1)*4]
        u = struct.unpack("<I", b)[0]
        f = struct.unpack("<f", b)[0]
        ch = "*" if w in words_changing else " "
        print(f"Word {w:3d} (0x{w*4:03x}) {ch}: hex={b.hex()}  u32={u:10d}  float={f:12.6f}")

if __name__ == "__main__":
    examine()
