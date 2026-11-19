"""Builds the PDFs the reflow tests read. Needs python-docx, reportlab and LibreOffice.

    python3 make_fixtures.py
"""
import os, struct, subprocess, tempfile, zlib
from docx import Document
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.shared import Pt, Cm
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.enums import TA_JUSTIFY
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import BaseDocTemplate, Frame, PageTemplate, Paragraph

HERE = os.path.dirname(os.path.abspath(__file__))

INTRO = ("The common garden snail moves at a pace that rewards patience. It glides on a thin film of "
         "mucus, which lets it cross rough bark and even the edge of a razor without harm. Most of its "
         "day is spent resting under leaves, and it becomes active at night or after rain, when the air "
         "is damp enough to keep its soft body from drying out.")
SHELL = ("Its shell grows with it for its whole life, adding new rings at the opening. Shells coil to the "
         "right in nearly every individual; a left-coiling snail is so rare that finding one is a small "
         "event for collectors.")
DIET = ("Snails eat leaves, fungi and decaying plants, scraping food with a ribbon of tiny teeth.")


def word_document():
    doc = Document()
    section = doc.sections[0]
    section.page_width, section.page_height = Cm(21), Cm(29.7)
    section.left_margin = section.right_margin = Cm(2.5)
    normal = doc.styles["Normal"]
    normal.font.name = "Liberation Serif"
    normal.font.size = Pt(11)
    normal.paragraph_format.space_after = Pt(8)
    normal.paragraph_format.line_spacing = 1.15

    title = doc.add_paragraph()
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = title.add_run("The Awkward Snail")
    run.bold = True
    run.font.size = Pt(18)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.JUSTIFY
    first, rest = INTRO.split("garden snail", 1)
    p.add_run(first)
    p.add_run("garden snail").bold = True
    a, b = rest.split("razor", 1)
    p.add_run(a)
    p.add_run("razor").italic = True
    p.add_run(b)

    p = doc.add_paragraph(SHELL)
    p.paragraph_format.first_line_indent = Cm(1)

    doc.add_paragraph(DIET)

    p = doc.add_paragraph("A large snail can carry about 10 times its own weight and lives for up to 5 years; a shell 3 cm wide covers about 7 cm")
    p.add_run("2").font.superscript = True
    p.add_run(" of ground.")

    for item in ["Snails seal the shell opening with a dried layer of mucus to survive long dry spells, and can stay inside for weeks.",
                 "They hibernate through winter in groups, often under stones or in loose soil."]:
        doc.add_paragraph(item, style="List Bullet")

    with tempfile.TemporaryDirectory() as tmp:
        png = os.path.join(tmp, "box.png")
        with open(png, "wb") as f:
            f.write(tiny_png())
        doc.add_picture(png, width=Cm(4))

    footer = section.footer.paragraphs[0]
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    footer.add_run("Page 1")

    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, "word-style.docx")
        doc.save(path)
        subprocess.run(["soffice", "--headless", "--convert-to", "pdf", "--outdir", HERE, path], check=True, capture_output=True)


def tiny_png():
    rows = b"".join(b"\x00" + bytes([40, 120, 200]) * 8 for _ in range(4))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 8, 4, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def two_columns():
    pdfmetrics.registerFont(TTFont("DejaVuSans", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"))
    style = ParagraphStyle("body", fontName="DejaVuSans", fontSize=10, leading=13, alignment=TA_JUSTIFY, spaceAfter=8)
    width, height = A4
    gutter, margin = 20, 50
    col = (width - 2 * margin - gutter) / 2
    frames = [Frame(margin, margin, col, height - 2 * margin, id="left"),
              Frame(margin + col + gutter, margin, col, height - 2 * margin, id="right")]
    doc = BaseDocTemplate(os.path.join(HERE, "two-columns.pdf"), pagesize=A4, pageTemplates=[PageTemplate(frames=frames)])
    story = [Paragraph(INTRO, style), Paragraph(SHELL, style), Paragraph(DIET, style)]
    from reportlab.platypus import FrameBreak
    story += [FrameBreak(), Paragraph("Right column. " + SHELL, style), Paragraph(DIET, style)]
    doc.build(story)


def rtf_sample():
    """A Word document saved as RTF by LibreOffice, using the formatting the importer supports."""
    from docx.enum.text import WD_BREAK, WD_COLOR_INDEX
    from docx.shared import RGBColor
    doc = Document()
    section = doc.sections[0]
    section.page_width, section.page_height = Cm(21), Cm(29.7)
    section.left_margin = section.right_margin = Cm(2.5)
    normal = doc.styles["Normal"]
    normal.font.name = "Liberation Serif"
    normal.font.size = Pt(11)
    normal.paragraph_format.space_after = Pt(6)

    heading = doc.add_paragraph()
    heading.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = heading.add_run("Snail Care Sheet")
    run.bold = True
    run.font.size = Pt(16)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.JUSTIFY
    p.add_run("Garden snails need a ")
    p.add_run("damp").bold = True
    p.add_run(" home, ")
    p.add_run("fresh").italic = True
    p.add_run(" food and ")
    p.add_run("calcium").underline = True
    p.add_run(" for their shells. Keep them ")
    r = p.add_run("away from salt")
    r.font.color.rgb = RGBColor(0xC0, 0x00, 0x00)
    p.add_run(", which harms them, and mist the tank ")
    r = p.add_run("every evening")
    r.font.highlight_color = WD_COLOR_INDEX.YELLOW
    p.add_run(". A tank of 20 litres suits two or three adults; the ideal humidity is about 80%")
    p.add_run("1").font.superscript = True
    p.add_run(".")

    for item in ["Cucumber and lettuce", "Cuttlefish bone for calcium", "Fresh water, changed daily"]:
        doc.add_paragraph(item, style="List Bullet")
    for item in ["Clean the tank weekly.", "Check for eggs in the soil."]:
        doc.add_paragraph(item, style="List Number")

    p = doc.add_paragraph()
    p.paragraph_format.tab_stops.add_tab_stop(Cm(5))
    p.add_run("Temperature:\t18-24 °C")

    p = doc.add_paragraph("This paragraph uses one-and-a-half line spacing so that there is more room between its lines, "
                          "which makes longer notes easier to read when printed.")
    p.paragraph_format.line_spacing = 1.5

    table = doc.add_table(rows=3, cols=2)
    table.style = "Table Grid"
    for row, (food, often) in zip(table.rows, [("Food", "How often"), ("Lettuce", "Daily"), ("Cuttlefish", "Always available")]):
        row.cells[0].text, row.cells[1].text = food, often

    with tempfile.TemporaryDirectory() as tmp:
        png = os.path.join(tmp, "box.png")
        with open(png, "wb") as f:
            f.write(tiny_png())
        doc.add_picture(png, width=Cm(3))

    p = doc.add_paragraph()
    p.add_run().add_break(WD_BREAK.PAGE)
    p = doc.add_paragraph("Second page: café, naïve, Москва.")

    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, "sample.docx")
        doc.save(path)
        subprocess.run(["soffice", "--headless", "--convert-to", "rtf", "--outdir", HERE, path], check=True, capture_output=True)


if __name__ == "__main__":
    word_document()
    two_columns()
    rtf_sample()
