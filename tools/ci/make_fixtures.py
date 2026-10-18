"""Deterministic original test data; never reads a user's MIDI/score collection."""
from pathlib import Path
import argparse
import hashlib
import json
import struct


def vlq(value):
    result = [value & 127]
    value >>= 7
    while value:
        result.insert(0, (value & 127) | 128)
        value >>= 7
    return bytes(result)


def create(destination):
    destination.mkdir(parents=True, exist_ok=True)
    conductor = b"\0\xff\x51\x03\x07\xa1\x20\0\xff\x58\x04\x04\x02\x18\x08\0\xff\x2f\0"
    melody = b"\0\xc0\0"
    for pitch in [60, 62, 64, 67, 65, 64, 62, 60] * 4:
        melody += b"\0\x90" + bytes([pitch, 85]) + vlq(480) + b"\x80" + bytes([pitch, 0])
    melody += b"\0\xff\x2f\0"
    midi = b"MThd" + struct.pack(">IHHH", 6, 1, 2, 480)
    midi += b"".join(b"MTrk" + struct.pack(">I", len(track)) + track for track in [conductor, melody])
    (destination / "synthetic.mid").write_bytes(midi)
    measures = []
    for index in range(1, 33):
        attributes = ('<attributes><divisions>1</divisions><key><fifths>0</fifths></key>'
                      '<time><beats>4</beats><beat-type>4</beat-type></time>'
                      '<clef><sign>G</sign><line>2</line></clef></attributes>' if index == 1 else '')
        page = '<print new-page="yes"/>' if index == 17 else ''
        measures.append(f'<measure number="{index}">{page}{attributes}'
                        '<note><pitch><step>C</step><octave>4</octave></pitch>'
                        '<duration>4</duration><type>whole</type></note></measure>')
    score = ('<?xml version="1.0" encoding="utf-8"?><score-partwise version="4.0">'
             '<work><work-title>Estudo sintético de integração</work-title></work>'
             # A4 declared explicitly: without it MuseScore picks the paper of the system locale (Letter on US servers).
             '<defaults><scaling><millimeters>7</millimeters><tenths>40</tenths></scaling>'
             '<page-layout><page-height>1697</page-height><page-width>1200</page-width></page-layout></defaults>'
             '<part-list><score-part id="P1"><part-name>Piano</part-name>'
             '<score-instrument id="I1"><instrument-name>Piano</instrument-name></score-instrument>'
             '<midi-instrument id="I1"><midi-channel>1</midi-channel><midi-program>1</midi-program></midi-instrument>'
             '</score-part></part-list><part id="P1">' + ''.join(measures) + '</part></score-partwise>')
    (destination / "synthetic.musicxml").write_text(score, encoding="utf-8")
    manifest = {"origin": "generated-by-tools/ci/make_fixtures.py", "personal_files": False,
                "files": {file.name: hashlib.sha256(file.read_bytes()).hexdigest()
                          for file in destination.iterdir() if file.suffix in (".mid", ".musicxml")}}
    (destination / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    create(parser.parse_args().destination.resolve())
