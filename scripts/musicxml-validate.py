#!/usr/bin/env python3
# Validates MusicXML files against the MusicXML 4.0 XSD checked in with the tests
# (engine/tests/Anode.Interchange.Tests/MusicXml/Schema/, docs/interchange/musicxml.md, "Validation"), with xmllint
# (libxml2). The schema's imports of xml.xsd and xlink.xsd are resolved to the local copies through an XML catalog, and
# xmllint runs with --nonet, so nothing is fetched. A .mxl container is opened through META-INF/container.xml (its root
# file), and the container document is checked against container.xsd too.
#
# Exit status 0 when every file is valid; 1 (with xmllint's messages) otherwise; 2 for a usage or setup error.
# The Phase 9 golden path (editor/apps/desktop/e2e/phase9/) runs it as ANODE_MUSICXML_CHECK.
#
# Usage: python3 scripts/musicxml-validate.py <file.musicxml|file.mxl> ...
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ElementTree
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCHEMA = ROOT / "engine" / "tests" / "Anode.Interchange.Tests" / "MusicXml" / "Schema"
CATALOG = """<?xml version="1.0"?>
<catalog xmlns="urn:oasis:names:tc:entity:xmlns:xml:catalog">
  <system systemId="http://www.musicxml.org/xsd/xml.xsd" uri="{xml}"/>
  <uri name="http://www.musicxml.org/xsd/xml.xsd" uri="{xml}"/>
  <system systemId="http://www.musicxml.org/xsd/xlink.xsd" uri="{xlink}"/>
  <uri name="http://www.musicxml.org/xsd/xlink.xsd" uri="{xlink}"/>
</catalog>
"""


def xmllint(schema, document, catalog):
    result = subprocess.run(
        ["xmllint", "--nonet", "--noout", "--schema", str(schema), str(document)],
        env={"XML_CATALOG_FILES": str(catalog), "PATH": "/usr/bin:/bin:/usr/local/bin:/opt/homebrew/bin"},
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        sys.stderr.write(result.stderr)
    return result.returncode == 0


def documents(path, scratch):
    """The documents to validate: a plain file itself; a container's container.xml and root score."""
    if not zipfile.is_zipfile(path):
        return [(SCHEMA / "musicxml.xsd", path)]
    with zipfile.ZipFile(path) as archive:
        container = scratch / f"{path.stem}-container.xml"
        container.write_bytes(archive.read("META-INF/container.xml"))
        rootfile = ElementTree.parse(container).getroot().find(".//rootfile")
        if rootfile is None:
            raise ValueError(f"{path}: META-INF/container.xml names no root file")
        score = scratch / f"{path.stem}-score.musicxml"
        score.write_bytes(archive.read(rootfile.get("full-path")))
    return [(SCHEMA / "container.xsd", container), (SCHEMA / "musicxml.xsd", score)]


def main(paths):
    if not paths:
        sys.stderr.write("Usage: musicxml-validate.py <file.musicxml|file.mxl> ...\n")
        return 2
    if shutil.which("xmllint") is None:
        sys.stderr.write("xmllint (libxml2) is not installed.\n")
        return 2
    with tempfile.TemporaryDirectory() as directory:
        scratch = Path(directory)
        catalog = scratch / "catalog.xml"
        catalog.write_text(
            CATALOG.format(xml=(SCHEMA / "xml.xsd").as_uri(), xlink=(SCHEMA / "xlink.xsd").as_uri()), encoding="utf-8"
        )
        valid = True
        for name in paths:
            path = Path(name)
            try:
                for schema, document in documents(path, scratch):
                    valid = xmllint(schema, document, catalog) and valid
            except (OSError, KeyError, ValueError, zipfile.BadZipFile, ElementTree.ParseError) as error:
                sys.stderr.write(f"{path}: {error}\n")
                valid = False
        return 0 if valid else 1


sys.exit(main(sys.argv[1:]))
