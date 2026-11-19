import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def find_first_shift():
    files = sorted([f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")])
    presets = []
    for f in files:
        raw = open(os.path.join(CORPUS_DIR, f), "rb").read()
        pos, body = nfm8_decode.find_compressed_body(raw)
        ver, hv, strs, p = nfm8_decode.parse_e8mf(body)
        presets.append((f, p))

    p2987 = [p for f, p in presets if len(p) == 2987][0]
    p3071 = [p for f, p in presets if len(p) == 3071][0] # diff is 84 = 7 * 12

    # Let's search for an identical chunk of bytes (e.g. 16 bytes) that appears at offset X in p2987
    # and at offset X + 84 in p3071!
    # If the difference is all in one place, or multiple places:
    print("Testing shift offsets:")
    matches = []
    for off in range(0, len(p2987) - 16, 4):
        target = p2987[off:off+16]
        # Does this exact 16-byte chunk appear in p3071 at off + 84?
        if p3071[off+84:off+84+16] == target and target != b'\x00'*16:
            matches.append((off, off+84))
    
    print(f"Total shifted matches with +84: {len(matches)}")
    if matches:
        print(f"First match with +84: p2987 at {matches[0][0]}, p3071 at {matches[0][1]}")

    # What about smaller shifts like +12, +24, +36...?
    for shift in range(12, 96, 12):
        s_matches = []
        for off in range(0, len(p2987) - 16, 4):
            target = p2987[off:off+16]
            if target != b'\x00'*16 and off + shift + 16 <= len(p3071):
                if p3071[off+shift:off+shift+16] == target:
                    s_matches.append((off, off+shift))
        if s_matches:
            print(f"Shift +{shift}: first match p2987 at {s_matches[0][0]}, p3071 at {s_matches[0][1]}")

if __name__ == "__main__":
    find_first_shift()
