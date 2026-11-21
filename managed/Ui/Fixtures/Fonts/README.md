# Font fixtures

Small cuts of real fonts for the font self-checks: each keeps the glyphs of `Hamburgefonstiv HOLx01`, every name
record, OS/2 and its variation tables. `tools/fonts/make-fixtures.py` cuts them and writes
`NotoSans-VF.golden.tsv`, fontTools' own instance of Noto Sans at each weight from 100 to 900.

- DejaVu Sans (`DejaVuSans*.ttf`, `DejaVuPair.ttc`): Bitstream Vera and DejaVu terms, `DejaVu-LICENSE.txt`.
- Noto Sans (`NotoSans-*.ttf`): Copyright 2022 The Noto Project Authors, SIL Open Font License 1.1, `OFL.txt`.
- Cantarell (`Cantarell-*`): Copyright 2019 The Cantarell Project Authors, SIL Open Font License 1.1, `OFL.txt`.

`DejaVuPair.ttc` stands in for a TrueType collection such as Windows' `cambria.ttc`, and `Cantarell-CFF2.ttc` for a
collection of CFF2 fonts such as Noto Sans CJK.
