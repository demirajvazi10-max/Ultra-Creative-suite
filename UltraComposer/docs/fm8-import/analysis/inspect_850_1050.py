import os, sys, struct
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import nfm8_decode

CORPUS_DIR = r"D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD"

def examine():
    path = os.path.join(CORPUS_DIR, "Amza MOSS 2.nfm8")
    raw = open(path, "rb").read()
    pos, body = nfm8_decode.find_compressed_body(raw)
    ver, hv, strs, p = nfm8_decode.parse_e8mf(body)

    # Let's inspect the block from 850 to 1100
    print("Offset 850 to 1050:")
    for i in range(850, 1050, 16):
        chunk = p[i:i+16]
        hex_str = " ".join(f"{b:02x}" for b in chunk)
        ascii_str = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        print(f"0x{i:04x} ({i:4d}): {hex_str:<48}  {ascii_str}")

if __name__ == "__main__":
    examine()
