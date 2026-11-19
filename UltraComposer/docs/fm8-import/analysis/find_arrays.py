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

    # Let's check how much of the tail matches in structure across ALL 61 presets.
    # Specifically, if there is a fixed tail of length T, then for all presets,
    # the tail starts at offset `len(p) - T`.
    # Let's inspect the types of values in the tail and in the head.
    
    # First, let's find the prefix where offsets are fixed.
    # Where does the variable-length section begin?
    # If we look at presets of different lengths, e.g. 2987 vs 3047 vs 3071 vs 3095,
    # let's see where the byte-shift occurs!
    p_2987 = [p for f, p in presets if len(p) == 2987][0]
    p_3047 = [p for f, p in presets if len(p) == 3047][0]
    p_3071 = [p for f, p in presets if len(p) == 3071][0]
    p_3095 = [p for f, p in presets if len(p) == 3095][0]
    p_4571 = [p for f, p in presets if len(p) == 4571][0]

    # Let's search for sequences of u32 counts in p_3071
    # What are all u32 values in p_3071?
    print("Scanning p_3071 for u32 values between 1 and 50:")
    for offset in range(0, len(p_3071) - 4, 4):
        val = struct.unpack_from("<I", p_3071, offset)[0]
        # check if it's a small integer 1..40
        if 1 <= val <= 40:
            # check the float representation
            flt = struct.unpack_from("<f", p_3071, offset)[0]
            # Print if flt is subnormal / denormal or very small float,
            # meaning it was written as an integer, not a float!
            # A float value for small ints:
            # 1 as u32 is 1 (as float: 1.4e-45)
            # 2 as u32 is 2 (as float: 2.8e-45)
            # Whereas 1.0f as u32 is 0x3f800000 = 1065353216!
            # If val is 1..40 and flt < 1e-30, it is DEFINITELY an integer!
            if flt < 1e-30:
                print(f"  Offset 0x{offset:04x} ({offset:5d}): integer {val}")

if __name__ == "__main__":
    examine()
