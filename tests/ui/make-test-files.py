import os, struct, zlib, pathlib
out = pathlib.Path(os.environ["TEMP"]) / "hsa-ui-files"
out.mkdir(exist_ok=True)

# --- a real 3-page text PDF (Helvetica), written by hand
def make_pdf(path, pages):
    objs = []
    def add(b): objs.append(b); return len(objs)
    add(b"<< /Type /Catalog /Pages 2 0 R >>")
    kids = " ".join(f"{4 + i * 2} 0 R" for i in range(pages))
    add(f"<< /Type /Pages /Count {pages} /Kids [{kids}] >>".encode())
    add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    for i in range(pages):
        content = f"BT /F1 36 Tf 72 700 Td (Hello page {i + 1} of {pages}) Tj ET".encode()
        add(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>".encode())
        add(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
    buf = bytearray(b"%PDF-1.4\n")
    offs = []
    for n, o in enumerate(objs, 1):
        offs.append(len(buf)); buf += f"{n} 0 obj\n".encode() + o + b"\nendobj\n"
    x = len(buf)
    buf += f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n".encode()
    for o in offs: buf += f"{o:010d} 00000 n \n".encode()
    buf += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{x}\n%%EOF\n".encode()
    path.write_bytes(bytes(buf))
make_pdf(out / "doc3.pdf", 3)

# --- a PNG photo (gradient)
w, h = 800, 600
raw = bytearray()
for y in range(h):
    raw.append(0)
    for x in range(w):
        raw += bytes((x * 255 // w, y * 255 // h, 160))
def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
(out / "photo.png").write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(raw))) + chunk(b"IEND", b""))

(out / "note.txt").write_text("plain text file\n")
print(out)
