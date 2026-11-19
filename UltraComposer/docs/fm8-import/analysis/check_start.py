import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def check_env_start():
    files = sorted([f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")])
    presets = []
    for f in files:
        raw = open(os.path.join(CORPUS_DIR, f), "rb").read()
        pos, body = nfm8_decode.find_compressed_body(raw)
        ver, hv, strs, p = nfm8_decode.parse_e8mf(body)
        presets.append((f, p))

    p2987 = [p for f, p in presets if len(p) == 2987][0]
    p3071 = [p for f, p in presets if len(p) == 3071][0]
    p4571 = [p for f, p in presets if len(p) == 4571][0]

    # Let's inspect the bytes from 0 to 1800 in p2987 vs p3071 vs p4571.
    # Where is the very first difference in length between p2987 and p3071?
    # Let's compare byte by byte from 0 up to 1800.
    # Since parameter values can differ, let's find the first byte where offsets shift.
    # If the prefix before the envelopes is FIXED LENGTH, what is that fixed length?
    
    # Notice in p3071, at 1768 we saw integer 2.
    # What is at 1768 in p2987?
    print(f"p2987 at 1768: {p2987[1768:1772].hex()}")
    print(f"p3071 at 1768: {p3071[1768:1772].hex()}")
    print(f"p4571 at 1768: {p4571[1768:1772].hex()}")

    # Let's check if the prefix 0..1768 has any variable length arrays or if it is exactly identical in size!
    # If the total length difference of p3071 - p2987 is 84, and the shift after 2160 is EXACTLY 84,
    # then 100% of the length difference (all 84 bytes) is between 1768 and 2160!
    # Because (end_shift - start_shift) = 84 - 0 = 84!
    # That means EVERYTHING before 1768 has ZERO length difference!
    print("Is the length before the envelope section completely fixed across all presets?!")

if __name__ == "__main__":
    check_env_start()
