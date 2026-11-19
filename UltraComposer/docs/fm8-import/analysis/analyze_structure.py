import os
import struct
import sys

# Import functions from nfm8_decode.py
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def main():
    files = [f for f in os.listdir(CORPUS_DIR) if f.lower().endswith(".nfm8")]
    print(f"Total files: {len(files)}")
    
    presets = []
    for fname in sorted(files):
        path = os.path.join(CORPUS_DIR, fname)
        with open(path, "rb") as f:
            data = f.read()
        pos, body = nfm8_decode.find_compressed_body(data)
        ver, hv, strs, params = nfm8_decode.parse_e8mf(body)
        presets.append({
            "name": fname,
            "ver": ver,
            "hv": hv,
            "strings": strs,
            "param_len": len(params),
            "params": params
        })
    
    print(f"Decoded {len(presets)} presets.")
    init_sound_presets = [p for p in presets if p["strings"] == ['init sound', 'init sound', 'init sound', 'init sound']]
    print(f"Presets with 4x 'init sound': {len(init_sound_presets)}")
    for p in init_sound_presets:
        print(f"  {p['name']:40s} : {p['param_len']} bytes")

    param_lens = [p["param_len"] for p in presets]
    print(f"Param len min: {min(param_lens)}, max: {max(param_lens)}")

if __name__ == "__main__":
    main()
